namespace CoopHatchAlert;

internal sealed class ModConfig
{
    public bool EnableNotifications { get; set; } = true;
    public bool NotifyWhenCoopIsFull { get; set; } = true;
    public bool EnableExteriorMarker { get; set; } = true;
    public bool EnableExteriorTooltip { get; set; } = true;
    public bool EnableIncubatorTooltip { get; set; } = true;
    public bool ShowEmptyIncubatorTooltip { get; set; } = true;
    public float MarkerScale { get; set; } = 3f;
}
