namespace FFmpegUtils.Models;

public sealed record ImageAddress(string Region, string NearbyAddress, string Detail)
{
    public const string Attribution = "Photon · © OpenStreetMap 贡献者";
}
