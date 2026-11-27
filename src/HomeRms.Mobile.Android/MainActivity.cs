using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace HomeRms.Mobile.Android;

[Activity(
    Label = "Home RMS",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const string PrefsName = "homerms_prefs";
    private const string KeyHubUrl = "hub_url";
    private const string KeyLang = "lang";
    private const string ChannelId = "homerms_alerts";

    private LinearLayout _connectPanel = null!;
    private LinearLayout _webPanel = null!;
    private EditText _urlBox = null!;
    private TextView _statusText = null!;
    private TextView _subTitleText = null!;
    private TextView _urlLabel = null!;
    private Button _connectBtn = null!;
    private Button _scanBtn = null!;
    private Button _langBtn = null!;
    private Button _refreshBtn = null!;
    private Button _switchBtn = null!;
    private bool _isHebrew;
    private WebView _webView = null!;
    private CancellationTokenSource? _alertPollCts;
    private readonly HashSet<string> _seenAlertIds = new(StringComparer.Ordinal);
    private int _notifId = 100;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Window?.SetStatusBarColor(Color.ParseColor("#0B0D12"));
        CreateNotificationChannel();
        RequestNotificationPermissionIfNeeded();

        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };
        root.SetBackgroundColor(Color.ParseColor("#0B0D12"));

        BuildConnectPanel();
        BuildWebPanel();

        root.AddView(_connectPanel);
        root.AddView(_webPanel);
        SetContentView(root);

        var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
        _isHebrew = prefs.GetString(KeyLang, "en") == "he";
        ApplyAndroidLanguage();

        var saved = prefs.GetString(KeyHubUrl, "");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            _urlBox.Text = saved;
            OpenHub(saved);
        }
    }

    private void BuildConnectPanel()
    {
        _connectPanel = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };
        _connectPanel.SetPadding(48, 96, 48, 48);

        var title = new TextView(this)
        {
            Text = "Home RMS",
            TextSize = 28,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(Color.ParseColor("#E7EAF0"));

        _subTitleText = new TextView(this)
        {
            Text = "Central Operations Mobile Console (.NET Android)",
            TextSize = 14
        };
        _subTitleText.SetTextColor(Color.ParseColor("#A7AFBE"));
        _subTitleText.SetPadding(0, 6, 0, 36);

        _urlLabel = new TextView(this)
        {
            Text = "Central Hub URL (PC running Home RMS Console)",
            TextSize = 13
        };
        _urlLabel.SetTextColor(Color.ParseColor("#A7AFBE"));
        _urlLabel.SetPadding(0, 0, 0, 8);

        _urlBox = new EditText(this)
        {
            Hint = "http://192.168.1.4:7480",
            TextSize = 16
        };
        _urlBox.SetTextColor(Color.ParseColor("#E7EAF0"));
        _urlBox.SetHintTextColor(Color.ParseColor("#697388"));
        _urlBox.SetBackgroundColor(Color.ParseColor("#151922"));
        _urlBox.SetPadding(24, 24, 24, 24);

        _connectBtn = new Button(this) { Text = "Connect to Hub" };
        _connectBtn.SetBackgroundColor(Color.ParseColor("#5D69EB"));
        _connectBtn.SetTextColor(Color.White);
        _connectBtn.Click += (_, _) =>
        {
            var raw = (_urlBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                raw = "http://" + raw;
            if (!raw.Contains(':', StringComparison.Ordinal) || raw.LastIndexOf(':') < 7)
                raw = raw.TrimEnd('/') + ":7480";
            OpenHub(raw);
        };

        _scanBtn = new Button(this) { Text = "Scan LAN for Hub (:7480)" };
        _scanBtn.SetBackgroundColor(Color.ParseColor("#1A1F2A"));
        _scanBtn.SetTextColor(Color.ParseColor("#E7EAF0"));
        _scanBtn.Click += async (_, _) => await ScanLanForHubAsync();

        _langBtn = new Button(this) { Text = "עברית / English" };
        _langBtn.SetBackgroundColor(Color.ParseColor("#151922"));
        _langBtn.SetTextColor(Color.ParseColor("#A7AFBE"));
        _langBtn.Click += (_, _) => ToggleAndroidLanguage();

        _statusText = new TextView(this)
        {
            Text = "Tip: Make sure 'Allow LAN access' is enabled in Server & PKI if connecting from another device.",
            TextSize = 13
        };
        _statusText.SetTextColor(Color.ParseColor("#7C8597"));
        _statusText.SetPadding(0, 24, 0, 0);

        var spacer1 = new Space(this) { LayoutParameters = new LinearLayout.LayoutParams(1, 24) };
        var spacer2 = new Space(this) { LayoutParameters = new LinearLayout.LayoutParams(1, 16) };
        var spacer3 = new Space(this) { LayoutParameters = new LinearLayout.LayoutParams(1, 16) };

        _connectPanel.AddView(title);
        _connectPanel.AddView(_subTitleText);
        _connectPanel.AddView(_urlLabel);
        _connectPanel.AddView(_urlBox);
        _connectPanel.AddView(spacer1);
        _connectPanel.AddView(_connectBtn);
        _connectPanel.AddView(spacer2);
        _connectPanel.AddView(_scanBtn);
        _connectPanel.AddView(spacer3);
        _connectPanel.AddView(_langBtn);
        _connectPanel.AddView(_statusText);
    }

    private void BuildWebPanel()
    {
        _webPanel = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
        };

        var bar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        bar.SetBackgroundColor(Color.ParseColor("#10131A"));
        bar.SetPadding(20, 12, 20, 12);

        var barTitle = new TextView(this)
        {
            Text = "Home RMS",
            TextSize = 16,
            Typeface = Typeface.DefaultBold,
            LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { Gravity = GravityFlags.CenterVertical }
        };
        barTitle.SetTextColor(Color.ParseColor("#E7EAF0"));

        _refreshBtn = new Button(this) { Text = "Refresh" };
        _refreshBtn.SetBackgroundColor(Color.ParseColor("#1A1F2A"));
        _refreshBtn.SetTextColor(Color.ParseColor("#E7EAF0"));
        _refreshBtn.Click += (_, _) => _webView.Reload();

        _switchBtn = new Button(this) { Text = "Hubs" };
        _switchBtn.SetBackgroundColor(Color.ParseColor("#1A1F2A"));
        _switchBtn.SetTextColor(Color.ParseColor("#E7EAF0"));
        _switchBtn.Click += (_, _) =>
        {
            _alertPollCts?.Cancel();
            _webPanel.Visibility = ViewStates.Gone;
            _connectPanel.Visibility = ViewStates.Visible;
        };

        var webLangBtn = new Button(this) { Text = "EN/עב" };
        webLangBtn.SetBackgroundColor(Color.ParseColor("#1A1F2A"));
        webLangBtn.SetTextColor(Color.ParseColor("#E7EAF0"));
        webLangBtn.Click += (_, _) => ToggleAndroidLanguage();

        bar.AddView(barTitle);
        bar.AddView(webLangBtn);
        bar.AddView(_refreshBtn);
        bar.AddView(_switchBtn);

        _webView = new WebView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f)
        };
        _webView.Settings.JavaScriptEnabled = true;
        _webView.Settings.DomStorageEnabled = true;
        _webView.Settings.BuiltInZoomControls = true;
        _webView.Settings.DisplayZoomControls = false;
        _webView.SetWebViewClient(new WebViewClient());
        CookieManager.Instance?.SetAcceptCookie(true);
        CookieManager.Instance?.SetAcceptThirdPartyCookies(_webView, true);

        _webPanel.AddView(bar);
        _webPanel.AddView(_webView);
    }

    private void ToggleAndroidLanguage()
    {
        _isHebrew = !_isHebrew;
        var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
        prefs.Edit()!.PutString(KeyLang, _isHebrew ? "he" : "en")!.Apply();
        ApplyAndroidLanguage();
    }

    private void ApplyAndroidLanguage()
    {
        if (_isHebrew)
        {
            _subTitleText.Text = "קונסולת ניהול לנייד (.NET Android)";
            _urlLabel.Text = "כתובת שרת ה-Central Hub (המחשב שמריץ את הקונסולה)";
            _connectBtn.Text = "התחבר ל-Hub";
            _scanBtn.Text = "סרוק רשת למציאת Hub (:7480)";
            _langBtn.Text = "Switch to English";
            _statusText.Text = "טיפ: ודא ש-'Allow LAN access' מופעל תחת Server & PKI.";
            _refreshBtn.Text = "רענן";
            _switchBtn.Text = "שרתים";
        }
        else
        {
            _subTitleText.Text = "Central Operations Mobile Console (.NET Android)";
            _urlLabel.Text = "Central Hub URL (PC running Home RMS Console)";
            _connectBtn.Text = "Connect to Hub";
            _scanBtn.Text = "Scan LAN for Hub (:7480)";
            _langBtn.Text = "עברית / English";
            _statusText.Text = "Tip: Make sure 'Allow LAN access' is enabled in Server & PKI if connecting from another device.";
            _refreshBtn.Text = "Refresh";
            _switchBtn.Text = "Hubs";
        }
    }

    private void OpenHub(string url)
    {
        var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
        prefs.Edit()!.PutString(KeyHubUrl, url)!.Apply();

        _connectPanel.Visibility = ViewStates.Gone;
        _webPanel.Visibility = ViewStates.Visible;
        _webView.LoadUrl(url);
        StartAlertPolling(url);
    }

    private async Task ScanLanForHubAsync()
    {
        _scanBtn.Enabled = false;
        _statusText.Text = "Scanning local subnet on port 7480...";
        try
        {
            var basePrefix = GetLocalSubnetPrefix();
            if (basePrefix is null)
            {
                _statusText.Text = "Could not determine local Wi-Fi/LAN subnet. Enter the Hub IP manually.";
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(900) };
            string? found = null;
            using var cts = new CancellationTokenSource();

            var tasks = Enumerable.Range(1, 254).Select(async i =>
            {
                if (cts.IsCancellationRequested) return;
                var candidate = $"http://{basePrefix}.{i}:7480";
                try
                {
                    using var res = await http.GetAsync(candidate + "/api/hub/status", cts.Token);
                    if (res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized)
                    {
                        found = candidate;
                        cts.Cancel();
                    }
                }
                catch { }
            });

            await Task.WhenAll(tasks);
            if (found is not null)
            {
                _urlBox.Text = found;
                _statusText.Text = $"Found Central Hub at {found}!";
                OpenHub(found);
            }
            else
            {
                _statusText.Text = $"No Central Hub found on {basePrefix}.1-254:7480.";
            }
        }
        finally
        {
            _scanBtn.Enabled = true;
        }
    }

    private static string? GetLocalSubnetPrefix()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 127) continue;
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
            }
        }
        return null;
    }

    private void StartAlertPolling(string baseUrl)
    {
        _alertPollCts?.Cancel();
        _alertPollCts = new CancellationTokenSource();
        var ct = _alertPollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    var cookie = CookieManager.Instance?.GetCookie(baseUrl);
                    if (string.IsNullOrWhiteSpace(cookie)) continue;

                    using var handler = new HttpClientHandler { UseCookies = false };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/api/alerts");
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);
                    using var res = await http.SendAsync(req, ct);
                    if (!res.IsSuccessStatusCode) continue;

                    var json = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        var ack = el.TryGetProperty("acknowledged", out var ackEl) && ackEl.GetBoolean();
                        if (string.IsNullOrEmpty(id) || ack || !_seenAlertIds.Add(id)) continue;

                        var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Alert" : "Alert";
                        var ep = el.TryGetProperty("endpointName", out var eEl) ? eEl.GetString() ?? "" : "";
                        var detail = el.TryGetProperty("detail", out var dEl) ? dEl.GetString() ?? "" : "";
                        RunOnUiThread(() => PostNotification($"{title} — {ep}", detail));
                    }
                }
                catch (System.OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var channel = new NotificationChannel(ChannelId, "Home RMS Alerts", NotificationImportance.High)
        {
            Description = "Real-time endpoint and blocked-site alerts from Home RMS Central Hub"
        };
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        mgr?.CreateNotificationChannel(channel);
    }

    private void RequestNotificationPermissionIfNeeded()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                RequestPermissions([global::Android.Manifest.Permission.PostNotifications], 101);
        }
    }

    private void PostNotification(string title, string text)
    {
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr is null) return;
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder.SetContentTitle(title)
               .SetContentText(text)
               .SetSmallIcon(global::Android.Resource.Drawable.IcDialogAlert)
               .SetAutoCancel(true);
        mgr.Notify(_notifId++, builder.Build());
    }

    public override void OnBackPressed()
    {
        if (_webPanel.Visibility == ViewStates.Visible && _webView.CanGoBack())
        {
            _webView.GoBack();
            return;
        }
        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        _alertPollCts?.Cancel();
        base.OnDestroy();
    }
}
