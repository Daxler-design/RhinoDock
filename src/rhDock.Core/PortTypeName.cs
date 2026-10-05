namespace rhDock.Core;

public static class PortTypeName
{
    public static string Element(string typeName)
    {
        var name = typeName.Trim();
        if (name.Contains('<')) name = name[(name.IndexOf('<') + 1)..].TrimEnd('>');
        else if (name.Contains('`') && name.Contains('[')) name = name[(name.IndexOf('[') + 1)..].TrimStart('[');
        return name.Split(',')[0].Replace("[]", "").TrimEnd(']').Trim();
    }
}
