using System.Runtime.InteropServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ForegroundControllerInput;

/// <summary>
/// Prevents Stardew Valley from receiving controller input while another process owns foreground focus.
/// This intentionally does not use Game1.IsActive, so mods which keep Stardew active in the background
/// (such as Better Always Active) don't defeat the controller-input block.
/// </summary>
internal sealed class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.UpdateTicking += this.OnUpdateTicking;
    }

    /// <summary>
    /// Immediately suppress a newly pressed controller input when Stardew isn't the foreground process.
    /// SMAPI suppression prevents the game itself from seeing the input and remains active until release.
    /// </summary>
    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (IsCurrentProcessForeground() || !IsControllerButton(e.Button))
            return;

        this.Helper.Input.Suppress(e.Button);
    }

    /// <summary>
    /// Also catches controller buttons/stick directions which were already held when focus was lost.
    /// This makes focus loss behave like the controller was disconnected from Stardew until focus returns.
    /// </summary>
    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || IsCurrentProcessForeground())
            return;

        foreach (SButton button in ControllerButtons.All)
        {
            if (this.Helper.Input.IsDown(button))
                this.Helper.Input.Suppress(button);
        }
    }

    private static bool IsCurrentProcessForeground()
    {
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return false;

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcessId);
        return foregroundProcessId == (uint)Environment.ProcessId;
    }

    private static bool IsControllerButton(SButton button)
    {
        string name = button.ToString();

        return name.StartsWith("Controller", StringComparison.Ordinal)
            || name.StartsWith("DPad", StringComparison.Ordinal)
            || name.StartsWith("LeftThumbstick", StringComparison.Ordinal)
            || name.StartsWith("RightThumbstick", StringComparison.Ordinal)
            || name is "LeftShoulder"
                or "RightShoulder"
                or "LeftStick"
                or "RightStick"
                or "LeftTrigger"
                or "RightTrigger"
                or "BigButton";
    }

    private static class ControllerButtons
    {
        internal static readonly SButton[] All = Enum
            .GetValues<SButton>()
            .Where(IsControllerButton)
            .ToArray();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
