namespace ZenRead.ViewModels;

public enum UiStateKind
{
    Loading,
    Empty,
    Error
}

public class UiStateViewModel
{
    public UiStateKind Kind { get; set; }

    public string Kicker { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string IconSvg { get; set; } = string.Empty;

    public string? PrimaryActionText { get; set; }

    public string? PrimaryActionUrl { get; set; }

    public string? SecondaryActionText { get; set; }

    public string? SecondaryActionUrl { get; set; }

    public string? Hint { get; set; }

    public int SkeletonCards { get; set; } = 4;

    public int SkeletonLines { get; set; } = 3;
}
