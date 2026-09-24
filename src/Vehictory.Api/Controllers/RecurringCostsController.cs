using Vehictory.Api.Data;
using Vehictory.Api.DTOs;
using Vehictory.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Vehictory.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/vehicles/{vehicleId:int}/recurring-costs")]
public class RecurringCostsController(VehictoryDbContext db) : ControllerBase
{
    // Eigenaar én iedereen met wie het voertuig gedeeld is, mogen vaste lasten lezen/beheren
    // (zelfde niveau als tankbeurten en onderhoud).
    private async Task<Vehicle?> GetAccessibleVehicle(int vehicleId)
    {
        var userId = this.GetUserId();
        return await db.Vehicles.SingleOrDefaultAsync(v =>
            v.Id == vehicleId && (v.UserId == userId || v.Shares.Any(s => s.UserId == userId)));
    }

    private static RecurringCostResponse ToResponse(RecurringCost r)
    {
        var vandaag = DateOnly.FromDateTime(DateTime.Today);
        var termijnen = r.Termijnen(vandaag).ToList();
        return new RecurringCostResponse(
            r.Id, r.VehicleId, r.Soort, r.Bedrag, r.Frequentie, r.Startdatum, r.Einddatum, r.Notitie,
            termijnen, r.Bedrag * termijnen.Count, r.VolgendeTermijn(vandaag));
    }

    private static string? Validate(RecurringCostRequest request)
    {
        if (request.Bedrag < 0) return "Bedrag kan niet negatief zijn.";
        if (request.Einddatum <= request.Startdatum) return "Einddatum moet na de startdatum liggen.";
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<List<RecurringCostResponse>>> GetAll(int vehicleId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var costs = await db.RecurringCosts
            .Where(r => r.VehicleId == vehicleId)
            .OrderBy(r => r.Soort).ThenByDescending(r => r.Startdatum)
            .ToListAsync();

        return Ok(costs.Select(ToResponse).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<RecurringCostResponse>> Create(int vehicleId, RecurringCostRequest request)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();
        if (Validate(request) is { } error) return BadRequest(error);

        var cost = new RecurringCost
        {
            VehicleId = vehicleId,
            Soort = request.Soort,
            Bedrag = request.Bedrag,
            Frequentie = request.Frequentie,
            Startdatum = request.Startdatum,
            Einddatum = request.Einddatum,
            Notitie = request.Notitie,
        };

        // Een nieuwe post (bv. een nieuwe verzekering) vervangt de lopende post van dezelfde soort:
        // die eindigt op de startdatum van de nieuwe, zodat termijnen niet dubbel geteld worden.
        var lopend = await db.RecurringCosts
            .Where(r => r.VehicleId == vehicleId && r.Soort == request.Soort
                && r.Startdatum < request.Startdatum
                && (r.Einddatum == null || r.Einddatum > request.Startdatum))
            .ToListAsync();
        foreach (var vorige in lopend) vorige.Einddatum = request.Startdatum;

        db.RecurringCosts.Add(cost);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { vehicleId }, ToResponse(cost));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<RecurringCostResponse>> Update(int vehicleId, int id, RecurringCostRequest request)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();
        if (Validate(request) is { } error) return BadRequest(error);

        var cost = await db.RecurringCosts.SingleOrDefaultAsync(r => r.Id == id && r.VehicleId == vehicleId);
        if (cost is null) return NotFound();

        cost.Soort = request.Soort;
        cost.Bedrag = request.Bedrag;
        cost.Frequentie = request.Frequentie;
        cost.Startdatum = request.Startdatum;
        cost.Einddatum = request.Einddatum;
        cost.Notitie = request.Notitie;
        await db.SaveChangesAsync();

        return Ok(ToResponse(cost));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int vehicleId, int id)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var cost = await db.RecurringCosts.SingleOrDefaultAsync(r => r.Id == id && r.VehicleId == vehicleId);
        if (cost is null) return NotFound();

        db.RecurringCosts.Remove(cost);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
