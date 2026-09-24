namespace Vehictory.Api.Models;

public enum RecurringCostSoort
{
    Wegenbelasting,
    Verzekering,
}

public enum RecurringCostFrequentie
{
    Maand,
    Kwartaal,
    Jaar,
}

// Terugkerende vaste last (wegenbelasting, verzekering). Termijnen worden niet opgeslagen maar
// afgeleid uit Startdatum + Frequentie, zodat een gewijzigde post altijd een kloppend overzicht geeft.
public class RecurringCost
{
    public int Id { get; set; }
    public required int VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }

    public required RecurringCostSoort Soort { get; set; }
    public required decimal Bedrag { get; set; }           // euro's per termijn
    public required RecurringCostFrequentie Frequentie { get; set; }
    public required DateOnly Startdatum { get; set; }      // datum van de eerste termijn
    public DateOnly? Einddatum { get; set; }               // exclusief: vanaf deze datum geen termijnen meer
    public string? Notitie { get; set; }

    private int MaandenPerTermijn => Frequentie switch
    {
        RecurringCostFrequentie.Maand => 1,
        RecurringCostFrequentie.Kwartaal => 3,
        _ => 12,
    };

    // Alle termijndatums t/m totEnMet en vóór Einddatum. Steeds vanaf Startdatum gerekend, zodat
    // bv. de 31e niet via 28 februari blijvend naar de 28e verschuift.
    public IEnumerable<DateOnly> Termijnen(DateOnly totEnMet)
    {
        for (var i = 0; ; i++)
        {
            var datum = Startdatum.AddMonths(i * MaandenPerTermijn);
            if (datum > totEnMet || (Einddatum is { } eind && datum >= eind)) yield break;
            yield return datum;
        }
    }

    public DateOnly? VolgendeTermijn(DateOnly vandaag)
    {
        var aantalVerlopen = Termijnen(vandaag).Count();
        var volgende = Startdatum.AddMonths(aantalVerlopen * MaandenPerTermijn);
        return Einddatum is { } eind && volgende >= eind ? null : volgende;
    }
}
