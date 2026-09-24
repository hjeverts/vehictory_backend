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

    // Total Cost of Ownership: afschrijving telt mee in de kosten. Zolang het voertuig niet verkocht is,
    // wordt degressief afgeschreven: elke volle maand sinds Aankoopdatum het percentage uit de Afschrijvingstabel
    // dat bij de leeftijd van de auto hoort, over de dan resterende waarde, tot de Restwaarde.
    // Na verkoop is de afschrijving het werkelijke waardeverlies: Aanschafprijs − Verkoopprijs.
    public decimal? Aanschafprijs { get; set; }         // euro's
    public decimal? Restwaarde { get; set; }            // minimale restwaarde in euro's; ondergrens
    // Opgeslagen als jsonb; kan null zijn bij voertuigen van vóór deze kolom.
    public List<AfschrijvingsStaffel>? Afschrijvingstabel { get; set; } = [];
    public DateOnly? Verkoopdatum { get; set; }
    public decimal? Verkoopprijs { get; set; }          // werkelijke verkoopprijs in euro's
    public byte[]? Foto { get; set; }
    public string? FotoContentType { get; set; }
    public byte[]? FotoThumbnail { get; set; }

    public ICollection<FuelEntry> FuelEntries { get; set; } = [];
    public ICollection<MaintenanceEntry> MaintenanceEntries { get; set; } = [];
    public ICollection<RecurringCost> RecurringCosts { get; set; } = [];
    public ICollection<VehicleShare> Shares { get; set; } = [];

    // Einddatum van de afschrijvingsperiode: vandaag, of de verkoopdatum als die eerder ligt.
    public DateOnly Peildatum(DateOnly vandaag) =>
        Verkoopdatum is { } verkocht && verkocht < vandaag ? verkocht : vandaag;

    // Leeftijd in hele jaren op een datum. Alleen het bouwjaar is bekend, dus de auto wordt op 1 januari een jaar ouder.
    public int? Leeftijd(DateOnly datum) => Bouwjaar is { } bouwjaar ? Math.Max(datum.Year - bouwjaar, 0) : null;

    // Percentage per maand voor een leeftijd: de staffel met de hoogste VanafLeeftijd ≤ leeftijd.
    public decimal PercentagePerMaand(int leeftijd) =>
        (Afschrijvingstabel ?? []).Where(s => s.VanafLeeftijd <= leeftijd).MaxBy(s => s.VanafLeeftijd)?.PercentagePerMaand ?? 0;

    // Maandelijkse afschrijvingstermijnen vanaf de maand na Aankoopdatum t/m de peildatum: telkens het percentage
    // bij de leeftijd op de termijndatum over de resterende waarde, zonder onder de Restwaarde te komen.
    // Datums steeds vanaf Aankoopdatum gerekend (zie RecurringCost.Termijnen).
    public IEnumerable<(DateOnly Datum, decimal Bedrag)> Afschrijvingstermijnen(DateOnly vandaag)
    {
        if (Aankoopdatum is not { } aankoop || Aanschafprijs is not { } prijs
            || Bouwjaar is null || Afschrijvingstabel is not { Count: > 0 }) yield break;

        var peildatum = Peildatum(vandaag);
        var restwaarde = Restwaarde ?? 0;
        var waarde = prijs;
        for (var i = 1; waarde > restwaarde; i++)
        {
            var datum = aankoop.AddMonths(i);
            if (datum > peildatum) yield break;
            var percentage = PercentagePerMaand(Leeftijd(datum)!.Value);
            var bedrag = Math.Min(waarde * percentage / 100, waarde - restwaarde);
            waarde -= bedrag;
            yield return (datum, bedrag);
        }
    }

    // Afschrijving t/m vandaag. Bij verkoop met bekende prijzen het werkelijke waardeverlies
    // (kan negatief zijn bij verkoop met winst).
    public decimal Afschrijving(DateOnly vandaag) =>
        Aanschafprijs is { } prijs && Verkoopdatum is not null && Verkoopprijs is { } verkoopprijs
            ? prijs - verkoopprijs
            : Afschrijvingstermijnen(vandaag).Sum(t => t.Bedrag);

    // Waarde van het voertuig: aanschafprijs minus afschrijving, of de verkoopprijs na verkoop.
    public decimal? Boekwaarde(DateOnly vandaag) =>
        Verkoopdatum is not null && Verkoopprijs is { } verkoopprijs ? verkoopprijs
        : Aanschafprijs is { } prijs ? prijs - Afschrijving(vandaag)
        : null;
}
