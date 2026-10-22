namespace TaskbarTiles.Contracts;

/// <summary>Marks a public ITileConnector class. config.json refers to it by <see cref="Id"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TileConnectorAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    /// <summary>Tile width in DIP at full size.</summary>
    public double Width { get; set; } = 132;
}
