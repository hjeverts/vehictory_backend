namespace Vehictory.Api.Models;

public class MaintenanceEntry
{
    public int Id { get; set; }
    public required int VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    public required DateOnly Datum { get; set; }
    public required int Odometer { get; set; }
    public required int MaintenanceTypeId { get; set; }
    public MaintenanceType? MaintenanceType { get; set; }
    public string? Notitie { get; set; }
    public decimal? Kosten { get; set; }               // euro's; optioneel

    public ICollection<MaintenanceAttachment> Attachments { get; set; } = [];
}
