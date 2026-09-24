namespace Vehictory.Api.Models;

public class Vehicle
{
    public int Id { get; set; }
    public required Guid UserId { get; set; }
    public User? User { get; set; }

    public required string Naam { get; set; }
    public string? Merk { get; set; }
    public string? Type { get; set; }
    public int? Bouwjaar { get; set; }
    public DateOnly? Aankoopdatum { get; set; }
    public byte[]? Foto { get; set; }
    public string? FotoContentType { get; set; }
    public byte[]? FotoThumbnail { get; set; }

    public ICollection<FuelEntry> FuelEntries { get; set; } = [];
    public ICollection<MaintenanceEntry> MaintenanceEntries { get; set; } = [];
    public ICollection<RecurringCost> RecurringCosts { get; set; } = [];
    public ICollection<VehicleShare> Shares { get; set; } = [];
}
