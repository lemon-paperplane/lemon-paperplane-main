using System;
using System.Linq;
using System.Windows;
using PaperplaneKit;
using static LemonPaperplane.UI;

namespace LemonPaperplane;

/// <summary>
/// 一条诊断：严重度图标与底色、标题、说明、可展开的详情、修复按钮（推荐的在前）。对应 Mac ProjectCard.swift 里的 DiagnosisRow。
/// compact：问题弹出框里用，无底色无描边、内边距小。
/// </summary>
public sealed class DiagnosisRow : PPView
{
    private readonly Diagnosis _d;
    private readonly string? _runId;
    private readonly string? _projectId;
    private readonly Action? _onFixed;
    private readonly bool _compact;
    private readonly State<bool> _expanded;

    public DiagnosisRow(Diagnosis diagnosis, string? runId = null, string? projectId = null, Action? onFixed = null, bool compact = false)
    {
        _d = diagnosis;
        _runId = runId;
        _projectId = projectId;
        _onFixed = onFixed;
        _compact = compact;
        _expanded = UseState(false);
    }

    private bool HasDetail => !string.IsNullOrEmpty(_d.Detail) || !string.IsNullOrEmpty(_d.Line);

    protected override UIElement Build()
    {
        var d = _d;
        var c = Theme.Severity(d.Severity);
        var icon = d.Severity == Severity.Error ? "circle-alert" : d.Severity == Severity.Warning ? "triangle-alert" : "info";
        var text = VStack(2, Paragraph(d.Title, Typo.BodyStrong),
            !string.IsNullOrEmpty(d.Message) ? Paragraph(d.Message, Typo.Small, ColorToken.TextSecondary) : null);
        var head = HStack(M.Sm,
            new PPIcon(icon, M.Icon, c.Fg).Top().Margins(0, 1, 0, 0),
            text.Flex(),
            HasDetail
                ? new PPIconButton(_expanded.Value ? "chevron-up" : "chevron-down", L.T("diagnosis.detail"), () => _expanded.Value = !_expanded.Value,
                    size: M.IconButtonSmall, iconSize: M.IconSmall).Top()
                : null);
        head.Align = CrossAlign.Start;
        var col = VStack(M.Sm, head);
        col.Align = CrossAlign.Stretch;
        if (_expanded.Value)
        {
            var detail = string.Join("\n\n", new[] { d.Detail, d.Line }.Where(s => !string.IsNullOrEmpty(s)));
            col.Children.Add(Box(Selectable(detail, Typo.MonoSmall, ColorToken.TextSecondary)).Pad(M.Sm).Rounded(M.RadiusSM).Bg(ColorToken.BgCode));
        }
        if (d.Fixes.Count > 0)
        {
            // 推荐的修复排在前面（同组保持原顺序）
            var fixes = d.Fixes.Select((f, i) => (f, i)).OrderBy(x => x.f.Recommended ? 0 : 1).ThenBy(x => x.i).Select(x => x.f);
            col.Children.Add(Flow(M.Sm, fixes.Select(f => new PPButton(f.Title,
                    () => Actions.ApplyFix(f, d, _runId, _projectId, _onFixed),
                    kind: f.Recommended ? PPButtonKind.Primary : PPButtonKind.Secondary, size: PPButtonSize.Small)))
                .Margins(M.Icon + M.Sm, 0, 0, 0));
        }
        var box = Box(col).Pad(_compact ? M.Sm : M.Md).Rounded(M.RadiusMD);
        if (!_compact) box.Bg(c.Bg).Outline(c.Border);
        return box.Id("diagnosis");
    }
}
