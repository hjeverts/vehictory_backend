using Vehictory.Api.Data;
using Vehictory.Api.DTOs;
using Vehictory.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Vehictory.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/vehicles/{vehicleId:int}/fuel")]
public class FuelEntriesController(VehictoryDbContext db) : ControllerBase
{
    // Eigenaar én iedereen met wie het voertuig gedeeld is, mogen tankbeurten lezen/toevoegen/verwijderen.
    private async Task<Vehicle?> GetAccessibleVehicle(int vehicleId)
    {
        var userId = this.GetUserId();
        return await db.Vehicles.SingleOrDefaultAsync(v =>
            v.Id == vehicleId && (v.UserId == userId || v.Shares.Any(s => s.UserId == userId)));
    }

    private static FuelEntryResponse ToResponse(FuelEntry f) => new(
        f.Id, f.VehicleId, f.Datum, f.Odometer, f.BrandstofType, f.Volume, f.Bedrag, f.Tankstation, f.Vergeten);

    [HttpGet]
    public async Task<ActionResult<List<FuelEntryResponse>>> GetAll(int vehicleId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var entries = await db.FuelEntries
            .Where(f => f.VehicleId == vehicleId)
            .OrderByDescending(f => f.Datum)
            .Select(f => ToResponse(f))
            .ToListAsync();

        return Ok(entries);
    }

    [HttpPost]
    public async Task<ActionResult<FuelEntryResponse>> Create(int vehicleId, FuelEntryRequest request)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var entry = new FuelEntry
        {
            VehicleId = vehicleId,
            Datum = request.Datum,
            Odometer = request.Odometer,
            BrandstofType = request.BrandstofType,
            Volume = request.Volume,
            Bedrag = request.Bedrag,
            Tankstation = request.Tankstation,
            Vergeten = request.Vergeten,
        };

        db.FuelEntries.Add(entry);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { vehicleId }, ToResponse(entry));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int vehicleId, int id)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var entry = await db.FuelEntries.SingleOrDefaultAsync(f => f.Id == id && f.VehicleId == vehicleId);
        if (entry is null) return NotFound();

        db.FuelEntries.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Vervangt de Dash plotly-visualisaties: geaggregeerde statistieken voor het dashboard.
    [HttpGet("/api/vehicles/{vehicleId:int}/stats")]
    public async Task<ActionResult<VehicleStatsResponse>> GetStats(int vehicleId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var entries = await db.FuelEntries
            .Where(f => f.VehicleId == vehicleId)
            .OrderBy(f => f.Odometer)
            .ToListAsync();
        var onderhoud = await db.MaintenanceEntries
            .Where(m => m.VehicleId == vehicleId)
            .Select(m => new { m.Datum, m.Kosten })
            .ToListAsync();
        var vasteLastenPosten = await db.RecurringCosts
            .Where(r => r.VehicleId == vehicleId)
            .ToListAsync();

        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var vasteLastenTermijnen = vasteLastenPosten
            .SelectMany(r => r.Termijnen(vandaag).Select(datum => new { Datum = datum, r.Bedrag }))
            .ToList();

        var brandstofKosten = entries.Sum(f => f.Bedrag);
        var onderhoudsKosten = onderhoud.Sum(m => m.Kosten ?? 0);
        var vasteLasten = vasteLastenTermijnen.Sum(t => t.Bedrag);
        var totaleKosten = brandstofKosten + onderhoudsKosten + vasteLasten;

        var totaalLiters = entries.Sum(f => f.Volume);
        var eersteOdometer = entries.FirstOrDefault()?.Odometer ?? 0;
        var laatsteOdometer = entries.LastOrDefault()?.Odometer ?? 0;
        var totaleAfstand = laatsteOdometer - eersteOdometer;
        var geldigeTankbeurten = entries
            .Skip(1)
            .Select((entry, index) => new { Entry = entry, Afstand = entry.Odometer - entries[index].Odometer })
            .Where(x => !x.Entry.Vergeten && x.Afstand > 0)
            .ToList();
        var afstand = geldigeTankbeurten.Sum(x => x.Afstand);
        var litersVoorVerbruik = geldigeTankbeurten.Sum(x => x.Entry.Volume);

        var verbruikL100km = afstand > 0 ? (litersVoorVerbruik / afstand) * 100m : 0;
        var gemPrijsPerLiter = totaalLiters > 0 ? brandstofKosten / totaalLiters : 0;
        decimal? kostenPerKm = totaleAfstand > 0 ? totaleKosten / totaleAfstand : null;

        // Kosten per jaar: totale kosten gedeeld door de periode van de eerste registratie t/m vandaag.
        // Onder de 30 dagen levert extrapoleren naar een jaar geen zinnig getal op.
        var eersteDatum = entries.Select(f => f.Datum)
            .Concat(onderhoud.Select(m => m.Datum))
            .Concat(vasteLastenTermijnen.Select(t => t.Datum))
            .DefaultIfEmpty(vandaag)
            .Min();
        var dagen = vandaag.DayNumber - eersteDatum.DayNumber;
        decimal? kostenPerJaar = dagen >= 30 ? totaleKosten / dagen * 365.25m : null;

        return Ok(new VehicleStatsResponse(
            vehicleId, totaleKosten, brandstofKosten, onderhoudsKosten, vasteLasten, totaalLiters,
            verbruikL100km, gemPrijsPerLiter, laatsteOdometer, totaleAfstand, kostenPerKm, kostenPerJaar));
    }
}
