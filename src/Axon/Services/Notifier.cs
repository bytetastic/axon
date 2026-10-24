using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace Axon.Services;

/// <summary>Tray-Symbol und Windows-Benachrichtigungen.</summary>
public sealed class Notifier : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    string? _pendingTab;

    /// <summary>Klick auf Symbol oder Benachrichtigung (Tab-Id oder null).</summary>
    public event Action<string?>? Activated;

    public Notifier()
    {
        _icon = new Forms.NotifyIcon
        {
            Text = "Axon",
            Visible = true,
        };
        try { _icon.Icon = Icon.ExtractAssociatedIcon(AppPaths.ExePath); } catch { _icon.Icon = SystemIcons.Application; }
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Activated?.Invoke(null); };
        _icon.BalloonTipClicked += (_, _) => Activated?.Invoke(_pendingTab);
    }

    public void Show(string title, string text, string? tabId)
    {
        _pendingTab = tabId;
        _icon.ShowBalloonTip(6000, title, string.IsNullOrWhiteSpace(text) ? " " : text, Forms.ToolTipIcon.None);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
