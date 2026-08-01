namespace PiRoundtable.Windows.Models;

public sealed record SelectionOptionItem(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
