using System.Windows;
using System.Windows.Controls;
using HomeRms.Controller.Services;

namespace HomeRms.Controller;

/// <summary>
/// Remote-desktop quality presets: LAN High Quality / Balanced / Low Bandwidth / Custom.
/// The first stream to an endpoint picks LAN High Quality when the agent is on a local subnet (or loopback)
/// and Balanced otherwise; after that the user's choice is remembered per endpoint in ui-settings.json.
/// </summary>
public partial class MainWindow
{
    private StreamPreset _streamPreset = StreamPreset.Balanced;
    private bool _suppressPresetChange;
    private IReadOnlyList<LocalSubnet>? _localSubnets;

    private void InitStreamPresets()
    {
        _suppressPresetChange = true;
        PresetCombo.Items.Clear();
        foreach (var p in StreamPresets.All)
            PresetCombo.Items.Add(new ComboBoxItem { Content = StreamPresets.Label(p), Tag = p, ToolTip = StreamPresets.Description(p) });
        SelectPreset(_streamPreset);
        _suppressPresetChange = false;
    }

    private void SelectPreset(StreamPreset preset)
    {
        _streamPreset = preset;
        foreach (ComboBoxItem item in PresetCombo.Items)
        {
            if (item.Tag is StreamPreset p && p == preset)
            {
                PresetCombo.SelectedItem = item;
                break;
            }
        }
        CustomStreamFields.Visibility = preset == StreamPreset.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? CurrentEndpointKey()
    {
        if (_conn is null) return null;
        return StreamPresets.EndpointKey(_connectedProfile?.ComputerName is { Length: > 0 } cn ? cn : _agentInfo?.MachineName,
            _conn.Host, _conn.Port);
    }

    /// <summary>After connecting: the saved preset for this endpoint, else the LAN / non-LAN default.</summary>
    private void ApplyStreamPresetForConnection()
    {
        if (_conn is null) return;
        try { _localSubnets ??= EndpointLocator.GetLocalSubnets(); }
        catch { _localSubnets = []; }

        var key = CurrentEndpointKey();
        var saved = key is not null && _ui.StreamPresetByEndpoint.TryGetValue(key, out var v) ? v : null;
        var preset = StreamPresets.Resolve(saved, _conn.RemoteAddress, _conn.Host, _localSubnets);

        _suppressPresetChange = true;
        try { SelectPreset(preset); }
        finally { _suppressPresetChange = false; }
    }

    private async void OnStreamPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ComboBoxItem { Tag: StreamPreset preset }) return;
        _streamPreset = preset;
        CustomStreamFields.Visibility = preset == StreamPreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (_suppressPresetChange) return;

        if (CurrentEndpointKey() is { } key && !IsScreenshotRun)
        {
            _ui.StreamPresetByEndpoint[key] = preset.ToString();
            _ui.Save();
        }

        await RestartStreamAsync($"Switching to {StreamPresets.Label(preset)}…");
    }

    /// <summary>What the next stream asks for: the preset, or the FPS / JPEG / resolution fields for Custom.</summary>
    private StreamSettings CurrentStreamSettings()
    {
        var fps = int.TryParse(FpsBox.Text, out var f) ? f : 30;
        var quality = int.TryParse(QualityBox.Text, out var q) ? q : 88;
        var maxWidth = ScaleBox.SelectedItem is ComboBoxItem { Tag: string ts } && int.TryParse(ts, out var mw) ? mw : 0;
        return StreamPresets.Settings(_streamPreset, fps, quality, maxWidth);
    }

    /// <summary>Restarts a running stream with the current settings; the last picture stays up until the new one arrives.</summary>
    private async Task RestartStreamAsync(string status)
    {
        if (_streamCts is null || _conn is null) return;
        StopStream();
        StreamStatus.Text = status;
        await Task.Delay(120); // let the agent release GPU capture of the old stream first
        if (_streamCts is null && _conn is not null)
            await StartStreamCoreAsync();
    }
}
