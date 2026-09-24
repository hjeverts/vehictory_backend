namespace Vehictory.Api.Models;

// Eén regel uit de afschrijvingstabel van een voertuig: vanaf deze leeftijd (in jaren, t.o.v. het bouwjaar)
// gaat er elke maand PercentagePerMaand van de resterende waarde af, tot de volgende staffel.
public class AfschrijvingsStaffel
{
    public int VanafLeeftijd { get; set; }
    public decimal PercentagePerMaand { get; set; }   // bv. 1.35 = 1,35% per maand
}
