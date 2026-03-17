namespace ZuikiProToTraincrew.Models;

public sealed class AppSettings
{
    public bool AlwaysSend { get; set; } = false;

    // key: "Btn0"..."Btn13", "SwUp", "SwDown"
    // value: InputAction の ToString() or "None"
    public Dictionary<string, string> ButtonMap { get; set; } = new();
}
