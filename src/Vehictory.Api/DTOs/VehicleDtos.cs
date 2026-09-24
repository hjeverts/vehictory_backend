using Vehictory.Api.Models;

namespace Vehictory.Api.DTOs;

public record VehicleRequest(
    string Naam,
    string? Merk,
    string? Type,
    int? Bouwjaar,
    DateOnly? Aankoopdatum,
    decimal? Aanschafprijs,
    decimal? Restwaarde,
    List<AfschrijvingsStaffelDto>? Afschrijvingstabel,
    DateOnly? Verkoopdatum,
    decimal? Verkoopprijs
);

// Staffel uit de afschrijvingstabel: vanaf deze leeftijd (jaren sinds bouwjaar) dit percentage per maand.
public record AfschrijvingsStaffelDto(int VanafLeeftijd, decimal PercentagePerMaand);

public record VehicleResponse(
    int Id,
    string Naam,
    string? Merk,
    string? Type,
    int? Bouwjaar,
    DateOnly? Aankoopdatum,
    decimal? Aanschafprijs,
    decimal? Restwaarde,
    List<AfschrijvingsStaffelDto> Afschrijvingstabel,
    DateOnly? Verkoopdatum,
    decimal? Verkoopprijs,
    bool IsOwner,
    string EigenaarNaam,
    string? FotoThumbnailDataUrl
);

public record ShareVehicleRequest(string Email);

public record VehicleShareResponse(Guid UserId, string Email, string Name, DateTime CreatedAt);

public record FuelEntryRequest(
    DateOnly Datum,
    int Odometer,
    string? BrandstofType,
    decimal Volume,
    decimal Bedrag,
    string? Tankstation,
    bool Vergeten
);

public record FuelEntryResponse(
    int Id,
    int VehicleId,
    DateOnly Datum,
    int Odometer,
    string? BrandstofType,
    decimal Volume,
    decimal Bedrag,
    string? Tankstation,
    bool Vergeten
);

public record MaintenanceEntryRequest(DateOnly Datum, int Odometer, int MaintenanceTypeId, string? Notitie, decimal? Kosten);

public record MaintenanceEntryResponse(
    int Id,
    int VehicleId,
    DateOnly Datum,
    int Odometer,
    int MaintenanceTypeId,
    string MaintenanceTypeNaam,
    string? Notitie,
    decimal? Kosten,
    List<MaintenanceAttachmentResponse> Attachments
);

// ThumbnailDataUrl is alleen gevuld voor afbeeldingen (IsImage); voor PDF's null.
// De volledige inhoud (foto of PDF) haal je op via GET .../maintenance/{id}/attachments/{attachmentId}
// zodat de lijst-response niet met meerdere MB's aan bijlagen wordt opgeblazen.
public record MaintenanceAttachmentResponse(
    int Id,
    string FileName,
    string ContentType,
    bool IsImage,
    string? ThumbnailDataUrl
);

public record MaintenanceTypeRequest(string Naam);
public record MaintenanceTypeResponse(int Id, string Naam);

public record RecurringCostRequest(
    RecurringCostSoort Soort,
    decimal Bedrag,
    RecurringCostFrequentie Frequentie,
    DateOnly Startdatum,
    DateOnly? Einddatum,
    string? Notitie
);

// Einddatum is exclusief (op die datum valt geen termijn meer). Bij aanmaken krijgt een lopende post van
// dezelfde soort de startdatum van de nieuwe als einddatum.
// Termijnen = alle automatisch afgeleide betaaldatums t/m vandaag; TotaalBetaald = Bedrag × aantal termijnen.
public record RecurringCostResponse(
    int Id,
    int VehicleId,
    RecurringCostSoort Soort,
    decimal Bedrag,
    RecurringCostFrequentie Frequentie,
    DateOnly Startdatum,
    DateOnly? Einddatum,
    string? Notitie,
    List<DateOnly> Termijnen,
    decimal TotaalBetaald,
    DateOnly? VolgendeTermijn
);

// Statistieken t.b.v. dashboard/grafieken (vervangt Dash-plotly visualisaties).
// TotaleKosten (Total Cost of Ownership) = brandstof + onderhoud + vaste lasten (t/m vandaag) + afschrijving.
// Afschrijving: per volle maand het percentage uit de afschrijvingstabel bij de leeftijd van de auto, over de dan
// resterende waarde (degressief) tot de restwaarde, of na verkoop aanschafprijs − verkoopprijs.
// Boekwaarde is null zonder aanschafprijs. TotaleAfstandKm = hoogste − laagste km-stand van de tankbeurten.
// KostenPerKm is null zolang er geen afstand is.
public record VehicleStatsResponse(
    int VehicleId,
    decimal TotaleKosten,
    decimal BrandstofKosten,
    decimal OnderhoudsKosten,
    decimal VasteLasten,
    decimal Afschrijving,
    decimal? Boekwaarde,
    decimal TotaalLiters,
    decimal GemiddeldeVerbruikL100km,
    decimal GemiddeldePrijsPerLiter,
    int LaatsteOdometer,
    int TotaleAfstandKm,
    decimal? KostenPerKm
);
