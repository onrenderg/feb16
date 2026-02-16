using System.Web;

namespace HppscAttendance;

public partial class FaceCapturePage : ContentPage
{
    public static string? CapturedImageBase64 { get; set; }
    public static bool WasCaptured { get; set; } = false;
    private string _referenceImage = "R0lGODdhAQABAPAAAP8AAAAAACwAAAAAAQABAAACAkQBADs=";
    public string _vectorImgFromPrevPage;

    public FaceCapturePage(string registeredImg, string vectorImgFromPrevPage)
    {
        _referenceImage = registeredImg;
        _vectorImgFromPrevPage = vectorImgFromPrevPage;
        InitializeComponent();
        //_referenceImage = registeredImg;
        LoadWebView();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        // Release camera when leaving page
        try
        {
            await webView.EvaluateJavaScriptAsync("stopCamera()");
        }
        catch { /* Ignore if JS not ready */ }
    }

    private void LoadWebView()
    {
#if ANDROID
        webView.Source = "file:///android_asset/wwwroot/index.html";
#else
        webView.Source = "wwwroot/index.html";
#endif
    }

    private void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("callback://"))
        {
            e.Cancel = true;

            try
            {
                var uri = new Uri(e.Url);
                var callbackType = uri.Host;
                var query = HttpUtility.ParseQueryString(uri.Query);

                switch (callbackType)
                {
                    case "match":
                        HandleMatchResult(
                            query["name"] ?? "",
                            query["confidence"] ?? "0",
                            query["isMatch"] == "true",
                            query["hasImage"] == "true"
                        );
                        break;
                    case "notmatch":
                        HandleMatchResult(
                            query["name"] ?? "",
                            query["confidence"] ?? "0",
                            false, // Fixed: was "isMatch" == "false" which is always false
                            query["hasImage"] == "true"
                        );
                        break;
                    case "detectonly":
                        // Detect-only mode: face detected, no matching performed
                        // Ensure hasvectorimage is "N" so ScanQrDetailsPage handles it correctly
                        // (covers case where registerExternalImageFromBase64 fell back to detect-only)
                        Preferences.Set("hasvectorimage", "N");
                        HandleMatchResult(
                            query["name"] ?? "DetectedFace",
                            query["confidence"] ?? "0",
                            false, // Consider detection as success
                            query["hasImage"] == "true"
                        );
                        break;

                    case "ready":
                        System.Diagnostics.Debug.WriteLine("[FaceApp] WebView ready");
                        // Register the reference image or vector passed from ScanQrDetailsPage
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            if (!string.IsNullOrEmpty(_vectorImgFromPrevPage))
                            {
                                Preferences.Set("hasvectorimage", "Y");
                                // Pass vector directly as array object (no quotes around the value)
                                await webView.EvaluateJavaScriptAsync($"registerExternalImage({_vectorImgFromPrevPage}, 'Reference')");

                            }
                            else if (!string.IsNullOrEmpty(_referenceImage))
                            {
                                // No vector but base64 image available - compute vector on WebView side
                                Preferences.Set("hasvectorimage", "Y");
                                var cleanBase64 = _referenceImage.Replace("\n", "").Replace("\r", "");
                                await webView.EvaluateJavaScriptAsync($"registerExternalImageFromBase64('{cleanBase64}', 'Reference')");
                            }
                            else
                            {
                                // No vector and no image - enable detect-only mode
                                Preferences.Set("hasvectorimage", "N");
                                await webView.EvaluateJavaScriptAsync("registerExternalImageNoVector()");
                            }
                            /*
                            else if (!string.IsNullOrEmpty(_referenceImage))
                            {
                                // Fallback to base64 image registration
                                var cleanBase64 = _referenceImage.Replace("\n", "").Replace("\r", "");
                                await webView.EvaluateJavaScriptAsync($"registerExternalImage('{cleanBase64}', 'Reference')");
                            }
                            */
                        });
                        break;

                    case "error":
                        System.Diagnostics.Debug.WriteLine($"[FaceApp] Error: {query["message"]}");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FaceApp] Callback error: {ex.Message}");
            }
        }
    }

    private async void HandleMatchResult(string name, string confidence, bool isMatch, bool hasImage)
    {

        Preferences.Set("hasimage", "scanned");
        if (hasImage)
        {
            var faceImageBase64 = await GetLastMatchImageAsync();
            if (!string.IsNullOrEmpty(faceImageBase64))
            {
                CapturedImageBase64 = faceImageBase64;
                string[] prefiximage = CapturedImageBase64.Split(',');
                string finalprefiximage = prefiximage[1];
                Preferences.Set("liveUserImg", finalprefiximage);

                WasCaptured = isMatch;
            }
        }

        // Navigate back to the previous page
        MainThread.BeginInvokeOnMainThread(async () =>
        {

            await Navigation.PopAsync();
        });
    }

    public async Task<string?> GetLastMatchImageAsync()
    {
        try
        {
            var result = await webView.EvaluateJavaScriptAsync("getLastMatchImage()");
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FaceApp] Error getting image: {ex.Message}");
            return null;
        }
    }
}