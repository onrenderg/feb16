# FaceCapturePage ↔ index.html — Complete Interaction Flow

---

## THE BIG PICTURE (One Sentence)

Another page (e.g. `ScanQrDetailsPage`) navigates to `FaceCapturePage`, passing a **base64 image** and/or a **vector array**. `FaceCapturePage` loads the HTML page inside a WebView, waits for it to become ready, then pushes the data into the HTML page via JavaScript. The HTML page then uses that data to decide **how** it should operate (match mode, computed-vector mode, or detect-only mode).

---

## STEP-BY-STEP FLOW

### ① Another Page Creates FaceCapturePage

Some other page in the app calls:

```csharp
new FaceCapturePage(registeredImg, vectorImgFromPrevPage)
```

- `registeredImg` → a **base64 string** of the person's registered photo (can be empty/null)
- `vectorImgFromPrevPage` → a **JSON array string** representing the face's 128-dimension vector (can be empty/null)

These get stored in the C# fields:
- `_referenceImage = registeredImg`
- `_vectorImgFromPrevPage = vectorImgFromPrevPage`

### ② Constructor Calls `LoadWebView()`

```
Constructor → InitializeComponent() → LoadWebView()
```

`LoadWebView()` sets the WebView's `Source` to `index.html`:
- Android: `file:///android_asset/wwwroot/index.html`
- Other: `wwwroot/index.html`

This tells the WebView to **start loading** the HTML page. At this point C# is done for now — it just waits.

---

### ③ HTML Page Starts: `init()` Runs IMMEDIATELY

As soon as `index.html` loads in the browser/WebView, the very last line of the `<script>` is:

```js
init();
```

**Inside `init()`, these things happen IN ORDER:**

1. **Load the local face database** (`loadDb()`) — reads any previously stored faces from `localStorage` (usually empty in this flow)

2. **Load the 3 AI models** (one after the other, sequentially):
   - `tinyFaceDetector` — for detecting faces in the camera feed
   - `faceLandmark68Net` — for finding eye/nose/mouth landmarks
   - `faceRecognitionNet` — for computing the 128-dimension face vector

3. **Start the camera** — requests `getUserMedia` and connects the camera stream to the `<video>` element

4. **Send `callback://ready`** — this is the **critical bridge moment**. The HTML page tells C# "I'm ready" by navigating to:
   ```
   callback://ready?status=initialized
   ```

5. **Start `detectLoop()`** — once the video has loaded its first frame (`video.onloadeddata`), the detection loop begins running continuously via `requestAnimationFrame`

> **KEY ANSWER TO YOUR QUESTION:** Yes, the model loading AND camera start happen **BEFORE** the `callback://ready` is sent, and the `detectLoop` starts right after (as soon as the first video frame arrives). The `detectLoop` runs **in parallel / independently** from whatever C# does next in step ④. They are not blocking each other.

---

### ④ C# Receives the "ready" Callback — THIS IS WHERE DATA GETS PASSED TO HTML

When the HTML page navigates to `callback://ready?...`, the WebView fires the `OnWebViewNavigating` event in C#. The C# code intercepts this URL (because it starts with `callback://`), cancels the actual navigation, and then **checks what data it has**:

#### Scenario A: Vector Array IS Available (`_vectorImgFromPrevPage` is not empty)

```csharp
// C# calls this JavaScript function:
await webView.EvaluateJavaScriptAsync($"registerExternalImage({_vectorImgFromPrevPage}, 'Reference')");
```

- Sets `Preferences("hasvectorimage") = "Y"`
- Notice: the vector is injected **directly as a JS array** (no quotes around it), e.g.:
  `registerExternalImage([0.123, -0.456, ...128 numbers...], 'Reference')`
- **On the HTML side**, `registerExternalImage()` sees `Array.isArray(data) === true`, so it:
  - Clears `facesDb`
  - Stores the vector directly: `facesDb['Reference'] = data`
  - Shows "✅ Reference Vector Loaded"
  - **Match mode is now active** — the detectLoop will compare the live face against this stored vector

#### Scenario B: No Vector, but Base64 Image IS Available (`_referenceImage` is not empty)

```csharp
// C# calls this JavaScript function:
await webView.EvaluateJavaScriptAsync($"registerExternalImageFromBase64('{cleanBase64}', 'Reference')");
```

- Sets `Preferences("hasvectorimage") = "Y"`
- **On the HTML side**, `registerExternalImageFromBase64()`:
  - Creates an `<img>` element from the base64 data
  - Uses `faceapi.detectSingleFace()` to **compute the 128-dim vector from the image**
  - If a face is found → stores the computed vector in `facesDb`, matching mode active
  - If NO face is found in the image → **falls back to detect-only mode** (sets `detectOnlyMode = true`)
  - If any error occurs → also falls back to detect-only mode

#### Scenario C: No Vector AND No Image (both empty/null)

```csharp
// C# calls this JavaScript function:
await webView.EvaluateJavaScriptAsync("registerExternalImageNoVector()");
```

- Sets `Preferences("hasvectorimage") = "N"`
- **On the HTML side**, `registerExternalImageNoVector()`:
  - Sets `detectOnlyMode = true`
  - Clears `facesDb`
  - Shows "📸 Detect-Only Mode: Close eyes to capture"
  - The page will now just **detect a face + blink**, capture a photo, and send it back — no identity matching

---

### ⑤ The `detectLoop` Is Already Running Throughout All of This

While step ④ is happening (C# pushing data into the HTML page), the `detectLoop` is already running in the background. Here's what it does **every frame** (via `requestAnimationFrame`):

1. Detect a face in the current video frame
2. If face found → check if eyes are closed (using landmark positions)
3. If eyes closed for 5+ consecutive frames (with same-face verification) → call `markAttendance()`

### ⑥ `markAttendance()` — The Final Action

When `markAttendance()` fires:

1. **Captures the current video frame** as a base64 JPEG image
2. **Checks the mode**:
   - **Detect-Only Mode** (`detectOnlyMode === true`): Sends `callback://detectonly` back to C# with the captured image
   - **Match Mode**: Compares the live face vector against `facesDb`, then:
     - If match found → sends `callback://match` with name, confidence, and `hasImage: true`
     - If no match → sends `callback://notmatch`

### ⑦ C# Handles the Result Callback

Back in `FaceCapturePage.xaml.cs`, the `OnWebViewNavigating` method catches the callback:

- For `match` / `notmatch` / `detectonly` → calls `HandleMatchResult()`
- `HandleMatchResult()`:
  1. Sets `Preferences("hasimage") = "scanned"`
  2. If `hasImage` is true → calls `getLastMatchImage()` via JavaScript to retrieve the captured base64 frame
  3. Stores it in `CapturedImageBase64` and `Preferences("liveUserImg")`
  4. **Navigates back** to the previous page (`Navigation.PopAsync()`)

The previous page can then read `FaceCapturePage.CapturedImageBase64` and `FaceCapturePage.WasCaptured` to know the result.

---

## TIMING SUMMARY (What Happens When)

```
┌─────────────────────────────────────────────────────────────────────┐
│ C# Side                          │ HTML/JS Side                    │
├──────────────────────────────────┼─────────────────────────────────┤
│ 1. Constructor called with       │                                 │
│    registeredImg + vector        │                                 │
│ 2. LoadWebView() → sets source   │                                 │
│    to index.html                 │                                 │
│                                  │ 3. init() starts                │
│                                  │ 4. Models load (3 models)       │
│                                  │ 5. Camera starts                │
│                                  │ 6. sendToMaui('ready')          │
│                                  │ 7. detectLoop() begins running  │
│ 8. OnWebViewNavigating catches   │    (runs continuously from now) │
│    "ready" callback              │                                 │
│ 9. Checks what data it has:      │                                 │
│    A) vector → calls             │                                 │
│       registerExternalImage()    │ → Stores vector, match mode ON  │
│    B) base64 → calls             │                                 │
│       registerExternalImageFrom  │ → Computes vector from image,   │
│       Base64()                   │   match mode ON (or fallback)   │
│    C) nothing → calls            │                                 │
│       registerExternalImageNo    │ → Detect-only mode ON           │
│       Vector()                   │                                 │
│                                  │                                 │
│         ... time passes, detectLoop is checking every frame ...    │
│                                  │                                 │
│                                  │ 10. User closes eyes → blink    │
│                                  │     detected → markAttendance() │
│                                  │ 11. Captures frame, sends       │
│                                  │     callback://match (or        │
│                                  │     detectonly or notmatch)      │
│ 12. HandleMatchResult() fires    │                                 │
│ 13. Retrieves captured image     │                                 │
│     via getLastMatchImage()      │                                 │
│ 14. Stores result, navigates     │                                 │
│     back to previous page        │                                 │
└──────────────────────────────────┴─────────────────────────────────┘
```

---

## KEY MECHANISM: How C# and HTML Talk to Each Other

There are exactly **two communication channels**:

### HTML → C# (Callbacks via URL navigation)
The HTML page changes `window.location.href` to a fake URL like `callback://ready?status=initialized`. The WebView catches this in `OnWebViewNavigating`, parses the URL, and acts on it. The actual navigation is **cancelled** (`e.Cancel = true`).

### C# → HTML (JavaScript injection)
C# calls `webView.EvaluateJavaScriptAsync("someJsFunction(args)")` which runs the JavaScript function directly inside the WebView. This is how C# pushes the reference image/vector into the HTML page.

---

## YOUR UNDERSTANDING — CONFIRMED AND CLARIFIED

> "base64 img and vector array is passed in constructor"

✅ **Correct.** Both are passed from the calling page into `FaceCapturePage(registeredImg, vectorImgFromPrevPage)`.

> "it is passed to HTML page — how, I don't know exactly"

✅ **Now you know.** It happens in the `case "ready"` block of `OnWebViewNavigating`. When HTML sends `callback://ready`, C# responds by calling one of three JavaScript functions via `EvaluateJavaScriptAsync()`.

> "based on condition, if both or just base64 or none, separate functions are called"

✅ **Correct.** The priority order is:
1. Vector exists → `registerExternalImage(vector, 'Reference')`
2. No vector but base64 exists → `registerExternalImageFromBase64(base64, 'Reference')`
3. Neither exists → `registerExternalImageNoVector()`

> "if both present, set function is called, but does loadModels and camera start happen before — till when, till detectLoop part?"

✅ **Yes!** Models load AND camera starts **BEFORE** the `callback://ready` is sent. The `detectLoop` starts immediately after (on first video frame). All of this happens **independently** of C# pushing the data. The `detectLoop` just keeps running, and once the reference data arrives (from step ④), `markAttendance()` will have the data it needs to do matching.
