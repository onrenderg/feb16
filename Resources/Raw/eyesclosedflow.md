# Eyes-Closed Detection → `markAttendance()` — Complete Flow

---

## THE BIG PICTURE (One Sentence)

Once `detectLoop()` is running, **every single frame** the system detects a face, checks if it's the **same person** as the previous frame, measures whether their eyes are closed, and only after **5 consecutive closed-eye frames from the same face** does it fire `markAttendance()` to capture the photo and send the result back to C#.

---

## STEP-BY-STEP FLOW

### ① `detectLoop()` Runs Every Frame via `requestAnimationFrame`

`detectLoop()` is called once when the video's first frame arrives:

```js
video.onloadeddata = () => detectLoop();
```

At the **end** of every call, it schedules itself again:

```js
requestAnimationFrame(detectLoop);
```

This means `detectLoop` runs **continuously** — once per animation frame (~60fps or whatever the device supports). It **never stops** unless the page is closed.

### ② Face Detection Happens First

Inside each frame, the **first thing** that happens is face detection:

```js
const detection = await faceapi.detectSingleFace(video, new faceapi.TinyFaceDetectorOptions())
    .withFaceLandmarks()
    .withFaceDescriptor();
```

This single call does **3 things at once**:

1. **`detectSingleFace()`** — finds one face in the current video frame (using TinyFaceDetector, the lightweight model)
2. **`.withFaceLandmarks()`** — locates 68 facial landmark points (eyes, nose, mouth, jawline)
3. **`.withFaceDescriptor()`** — computes the **128-dimension vector** (the face's unique "fingerprint")

**If NO face is detected**, the UI shows `❓ No face` and the loop moves on to the next frame. Nothing else happens.

**If a face IS detected**, we move to step ③.

### ③ `detectEyesClosed()` Is Called — This Is Where the Magic Happens

```js
const closed = detectEyesClosed(detection.landmarks, Array.from(detection.descriptor));
```

This function does **two separate checks** in order:

---

#### ③-A: Same-Face Verification (`isSameFace()`)

**Before** checking if eyes are closed, the system first checks: **is this the same person as the previous frame?**

```js
if (!isSameFace(descriptor)) {
    return false; // Face switched, reset and skip
}
```

**Why?** To prevent someone from swapping faces mid-blink. If person A closes their eyes and person B quickly jumps in front of the camera, the system would be fooled without this check.

**How `isSameFace()` works:**

```js
let lastFaceDescriptor = null;
const SAME_FACE_THRESHOLD = 0.4;

function isSameFace(currentDescriptor) {
    if (!lastFaceDescriptor) {
        lastFaceDescriptor = currentDescriptor;
        return true; // First detection, consider it valid
    }

    const distance = euclideanDistance(currentDescriptor, lastFaceDescriptor);

    if (distance < SAME_FACE_THRESHOLD) {
        lastFaceDescriptor = currentDescriptor; // Update for next frame
        return true;
    } else {
        // Different face detected - reset tracking
        lastFaceDescriptor = currentDescriptor;
        eyesClosedFrameCount = 0; // Reset eye counter
        return false;
    }
}
```

**Step-by-step logic:**

1. **First ever frame** → `lastFaceDescriptor` is `null` → store this face as the baseline → return `true`
2. **Subsequent frames** → compute **euclidean distance** between current face vector and previous face vector
3. **Distance < 0.4** → same person → update the stored descriptor → return `true`
4. **Distance ≥ 0.4** → **different person** → reset `lastFaceDescriptor` to the new face → **reset `eyesClosedFrameCount` to 0** → return `false`

> **KEY POINT:** When a different face appears, the eye-closed counter is **completely reset**. This means the new person has to keep their eyes closed for the full 5 frames from scratch.

> **KEY POINT:** `euclideanDistance` computes the straight-line distance between two 128-dimension vectors. Low distance = same face, high distance = different face.

```js
function euclideanDistance(a, b) {
    return Math.sqrt(a.reduce((sum, v, i) => sum + Math.pow(v - b[i], 2), 0));
}
```

---

#### ③-B: Eye Closure Detection (Landmark-Based)

Only if `isSameFace()` returned `true`, the system proceeds to check the eyes:

```js
const leftEye = landmarks.getLeftEye();
const rightEye = landmarks.getRightEye();
const leftDist = Math.abs(leftEye[1].y - leftEye[5].y);
const rightDist = Math.abs(rightEye[1].y - rightEye[5].y);

const eyesLookClosed = (leftDist < 5 && rightDist < 5);
```

**How eye landmark positions work:**

The 68-landmark model gives 6 points per eye (indices 0-5):

```
     1
  0     2       ← upper eyelid
  5     3       ← lower eyelid
     4
```

- Points **1** and **5** represent the top and bottom of the eye vertically
- `leftEye[1].y - leftEye[5].y` = **vertical distance** between upper and lower eyelid
- When eyes are **open** → distance is larger (e.g. 10-20 pixels)
- When eyes are **closed** → distance shrinks to very small (< 5 pixels)

**Both eyes must be closed** — `leftDist < 5 AND rightDist < 5`

---

#### ③-C: Consecutive Frame Counter

A single frame of "eyes closed" is **not enough**. The system requires **5 consecutive frames**:

```js
let eyesClosedFrameCount = 0;
const REQUIRED_CLOSED_FRAMES = 5;

if (eyesLookClosed) {
    eyesClosedFrameCount++;
} else {
    eyesClosedFrameCount = 0; // Reset counter when eyes not clearly closed
}

return eyesClosedFrameCount >= REQUIRED_CLOSED_FRAMES;
```

**Frame-by-frame example:**

```
Frame 1: Eyes open    → counter = 0 → return false
Frame 2: Eyes closed  → counter = 1 → return false
Frame 3: Eyes closed  → counter = 2 → return false
Frame 4: Eyes open    → counter = 0 → return false  ← RESET!
Frame 5: Eyes closed  → counter = 1 → return false
Frame 6: Eyes closed  → counter = 2 → return false
Frame 7: Eyes closed  → counter = 3 → return false
Frame 8: Eyes closed  → counter = 4 → return false
Frame 9: Eyes closed  → counter = 5 → return TRUE ✅ → markAttendance fires!
```

> **KEY POINT:** If someone blinks naturally (quick open-close), the counter resets before reaching 5. The user must **intentionally hold** their eyes closed for ~5 frames (~83ms at 60fps) to trigger attendance.

> **KEY POINT:** If a different face appears at ANY point during the counting, `isSameFace()` resets `eyesClosedFrameCount` to 0, so the new face must start from scratch.

---

### ④ Back in `detectLoop()` — Cooldown Check Before `markAttendance()`

Once `detectEyesClosed()` returns `true`, the loop does **one more check** before calling `markAttendance()`:

```js
if (closed) {
    document.getElementById('blinkCount').innerText = '😑 Eyes Closed!';
    if ((now - lastAttendanceTime) > COOLDOWN) {
        lastAttendanceTime = now;
        markAttendance(Array.from(detection.descriptor));
    }
} else {
    document.getElementById('blinkCount').innerText = '👀 Eyes Open';
}
```

**Cooldown mechanism:**

- `lastAttendanceTime` stores the timestamp of the last `markAttendance()` call
- `COOLDOWN = 5000` (5 seconds)
- `markAttendance()` only fires if **5 seconds** have passed since the last one
- This prevents rapid-fire duplicate attendance marks if the user keeps their eyes closed

### ⑤ `markAttendance()` — The Final Action

When all conditions are met (same face + 5 closed frames + cooldown passed), `markAttendance(descriptor)` fires:

#### Step 1: Capture the Current Frame

```js
lastMatchImage = captureFrame();
```

`captureFrame()` creates a temporary `<canvas>`, draws the current video frame onto it, and exports it as a **base64 JPEG** string:

```js
function captureFrame() {
    const video = document.getElementById('video');
    const canvas = document.createElement('canvas');
    canvas.width = video.videoWidth || 320;
    canvas.height = video.videoHeight || 240;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL('image/jpeg', 0.8);
}
```

The image is stored in `lastMatchImage` so C# can retrieve it later via `getLastMatchImage()`.

#### Step 2: Check Which Mode We're In

**Mode A — Detect-Only Mode** (`detectOnlyMode === true`):

```js
if (detectOnlyMode) {
    showResult('📸 Face Captured!', 'success');
    sendToMaui('detectonly', {
        name: 'DetectedFace',
        confidence: '100',
        isMatch: 'true',
        hasImage: 'true'
    });
    return;
}
```

- No identity matching is performed
- Simply sends `callback://detectonly` back to C# with `hasImage: true`
- C# will then call `getLastMatchImage()` to retrieve the captured frame

**Mode B — Match Mode** (reference vector exists in `facesDb`):

First, checks if `facesDb` has any entries:

```js
if (Object.keys(facesDb).length === 0) {
    showResult('⏳ Wait for reference to load...', 'info');
    return; // Bail out — reference not loaded yet
}
```

Then performs the actual face matching:

```js
const match = findMatch(descriptor);
```

**`findMatch()` logic:**

```js
function findMatch(descriptor, threshold = MATCH_THRESHOLD) {
    let best = { name: null, distance: Infinity };
    for (const [name, stored] of Object.entries(facesDb)) {
        const dist = euclideanDistance(descriptor, stored);
        if (dist < best.distance) best = { name, distance: dist };
    }
    return best.distance < threshold ? best : { name: null, distance: best.distance };
}
```

- Loops through all stored face vectors in `facesDb`
- Computes euclidean distance between the live face and each stored face
- Returns the **closest match** if distance < `MATCH_THRESHOLD` (0.5)
- Returns `{ name: null }` if no match is close enough

**If match found** (distance < 0.5):

```js
sendToMaui('match', {
    name: match.name,
    confidence: confidence,
    isMatch: 'true',
    hasImage: 'true'
});
```

**If no match** (distance ≥ 0.5):

```js
sendToMaui('notmatch', {
    name: match.name,
    confidence: '0',
    isMatch: 'false',
    hasImage: 'true'
});
```

### ⑥ C# Receives the Callback and Retrieves the Image

Back in `FaceCapturePage.xaml.cs`, the `OnWebViewNavigating` method catches the callback URL (`callback://match`, `callback://detectonly`, or `callback://notmatch`) and calls `HandleMatchResult()`:

1. Sets `Preferences("hasimage") = "scanned"`
2. Calls `getLastMatchImage()` via JavaScript to retrieve the base64 frame
3. Stores it in `CapturedImageBase64` and `Preferences("liveUserImg")`
4. **Navigates back** to the previous page (`Navigation.PopAsync()`)

---

## COMPLETE DECISION FLOW (Every Frame)

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         detectLoop() STARTS                             │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  1. Detect face in current video frame                                   │
│     └── No face? → Show "❓ No face" → SKIP to next frame               │
│                                                                          │
│  2. Face found! Call detectEyesClosed(landmarks, descriptor)             │
│     │                                                                    │
│     ├── 2a. isSameFace(descriptor)?                                      │
│     │       └── NO (distance ≥ 0.4)?                                     │
│     │           → Reset lastFaceDescriptor to new face                   │
│     │           → Reset eyesClosedFrameCount = 0                         │
│     │           → return false → Show "👀 Eyes Open" → next frame        │
│     │                                                                    │
│     │       └── YES (distance < 0.4)?                                    │
│     │           → Update lastFaceDescriptor                              │
│     │           → Continue to eye check ↓                                │
│     │                                                                    │
│     ├── 2b. Check eye landmarks                                          │
│     │       leftDist = |leftEye[1].y - leftEye[5].y|                     │
│     │       rightDist = |rightEye[1].y - rightEye[5].y|                  │
│     │                                                                    │
│     │       └── Eyes OPEN (leftDist ≥ 5 OR rightDist ≥ 5)?              │
│     │           → eyesClosedFrameCount = 0                               │
│     │           → return false → Show "👀 Eyes Open" → next frame        │
│     │                                                                    │
│     │       └── Eyes CLOSED (leftDist < 5 AND rightDist < 5)?           │
│     │           → eyesClosedFrameCount++                                 │
│     │           → Continue to threshold check ↓                          │
│     │                                                                    │
│     └── 2c. eyesClosedFrameCount >= 5?                                   │
│             └── NO → return false → Show "😑 Eyes Closed!" → next frame  │
│             └── YES → return true ↓                                      │
│                                                                          │
│  3. Eyes confirmed closed! Check cooldown                                │
│     └── (now - lastAttendanceTime) ≤ 5000ms?                            │
│         → SKIP → Show "😑 Eyes Closed!" → next frame                    │
│                                                                          │
│     └── Cooldown passed? → CALL markAttendance(descriptor) ↓            │
│                                                                          │
│  4. markAttendance(descriptor):                                          │
│     ├── Capture video frame → base64 JPEG → store in lastMatchImage     │
│     │                                                                    │
│     ├── Detect-Only Mode?                                                │
│     │   → sendToMaui('detectonly', { hasImage: true })                   │
│     │                                                                    │
│     ├── Match Mode (facesDb has entries)?                                 │
│     │   ├── findMatch(descriptor) → compare with stored vectors          │
│     │   ├── Match found (distance < 0.5)?                                │
│     │   │   → sendToMaui('match', { name, confidence, hasImage: true })  │
│     │   └── No match?                                                    │
│     │       → sendToMaui('notmatch', { hasImage: true })                 │
│     │                                                                    │
│     └── No references loaded?                                            │
│         → Show "⏳ Wait for reference to load..." → bail out             │
│                                                                          │
│  5. C# receives callback → HandleMatchResult()                          │
│     → Retrieves image via getLastMatchImage()                            │
│     → Stores result and navigates back                                   │
│                                                                          │
│  ──── requestAnimationFrame(detectLoop) → NEXT FRAME ────               │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## STATE VARIABLES INVOLVED

| Variable | Type | Initial Value | Purpose |
|---|---|---|---|
| `eyesClosedFrameCount` | `Number` | `0` | Counts consecutive frames where eyes are closed |
| `REQUIRED_CLOSED_FRAMES` | `const Number` | `5` | Threshold — how many frames eyes must be closed |
| `lastFaceDescriptor` | `Array\|null` | `null` | 128-dim vector of the face from the previous frame |
| `SAME_FACE_THRESHOLD` | `const Number` | `0.4` | Max euclidean distance to consider same face |
| `lastAttendanceTime` | `Number` | `0` | Timestamp of last `markAttendance()` call |
| `COOLDOWN` | `const Number` | `5000` | Milliseconds to wait between attendance marks |
| `lastMatchImage` | `String\|null` | `null` | Base64 JPEG of the last captured frame |
| `detectOnlyMode` | `Boolean` | `false` | Whether to skip matching and just capture |
| `facesDb` | `Object` | `{}` | Stored face vectors for matching (`{ name: vector }`) |
| `MATCH_THRESHOLD` | `const Number` | `0.5` | Max distance to consider a face match |

---

## RESET CONDITIONS SUMMARY

| Event | What Gets Reset |
|---|---|
| **Different face appears** (distance ≥ 0.4) | `eyesClosedFrameCount → 0`, `lastFaceDescriptor → new face` |
| **Eyes open for even 1 frame** | `eyesClosedFrameCount → 0` |
| **No face detected** | Nothing resets (counter stays, waiting for face to return) |
| **`markAttendance()` fires** | `lastAttendanceTime → now` (cooldown starts) |

> **IMPORTANT SUBTLETY:** When no face is detected, the `eyesClosedFrameCount` is **NOT** reset. This is because the detection simply skips the `detectEyesClosed()` call entirely. However, the next time a face appears, `isSameFace()` will compare it against `lastFaceDescriptor` — if it's a different person, the counter resets.

---

## WHY THIS ALSO PREVENTS FALSE BLINKS FROM DISTANCE (Moving Away From Camera)

### The Problem

The eye-closed check uses a **fixed pixel threshold** (`< 5 pixels`). This creates a risk:

```
Close to camera:  Eyes OPEN  → leftDist = 15px, rightDist = 14px  → ✅ Correctly detected as OPEN
Far from camera:  Eyes OPEN  → leftDist = 3px,  rightDist = 3px   → ❌ FALSELY detected as CLOSED!
```

When you move **far from the camera**, your face becomes **smaller** in the video frame. The eye landmarks get **compressed** — even with eyes wide open, the vertical distance between eyelid points shrinks below 5 pixels. Without protection, this would **falsely trigger** `markAttendance()`.

### How the System Prevents This (3 Layers of Protection)

#### Layer 1: `TinyFaceDetector` Stops Detecting Small Faces

The face detector has a **minimum face size** it can reliably detect. When you move far enough away:

```
detectSingleFace(video, new faceapi.TinyFaceDetectorOptions())
→ returns null (no face found)
→ detectLoop shows "❓ No face"
→ detectEyesClosed() is NEVER called
→ No false blink possible
```

This is the **first line of defense** — if the face is too small/far, the entire blink pipeline is skipped.

#### Layer 2: `isSameFace()` Detects the Descriptor Shift

When you **gradually** move away from the camera, the face descriptor (128-dim vector) changes because:
- The face occupies fewer pixels → less detail for the AI to work with
- The computed vector shifts compared to when you were close

If the shift is large enough (distance ≥ 0.4):

```
isSameFace(descriptor) → returns false
→ eyesClosedFrameCount resets to 0
→ Even if eyes "look" closed at this distance, counter starts over
```

This means moving away **resets the blink counter**, making it much harder for a false trigger to accumulate 5 consecutive frames.

#### Layer 3: The 5-Frame Consecutive Requirement Itself

Even if layers 1 and 2 don't catch it, the **5 consecutive frames** requirement still helps:

- As you move, the face size fluctuates frame-to-frame
- Some frames: eye distance dips below 5 (false closed)
- Other frames: eye distance is slightly above 5 (correctly open)
- This fluctuation **keeps resetting** `eyesClosedFrameCount` before it reaches 5

A false trigger would require you to be at **exactly** the wrong distance for 5 straight frames — which is unlikely during natural movement.

### Visual Summary

```
User moves away from camera:
──────────────────────────────────────────────────────────────

Distance:  CLOSE ──────────────────────────────► FAR

Face size: ██████████  →  ████████  →  ████  →  ██  →  (gone)

Eye dist:  15px (open) →  10px     →  5px   →  3px →  N/A

What happens at each stage:
  15px  → Eyes correctly detected OPEN ✅
  10px  → Eyes correctly detected OPEN ✅
   5px  → Borderline — might flicker between open/closed
   3px  → Would falsely show "closed" BUT:
          • isSameFace() may detect descriptor drift → RESET counter
          • Frame-to-frame fluctuation → counter keeps resetting
   N/A  → Face too small for TinyFaceDetector → NO detection at all

Result: markAttendance() does NOT fire from distance alone ✅
```

> **BOTTOM LINE:** The `< 5 pixel` threshold is a crude measurement that works well **at the intended usage distance** (arm's length from phone camera). The same-face verification, face detection limits, and consecutive frame requirement together create a **safety net** that prevents false triggers when the user is too far away.
