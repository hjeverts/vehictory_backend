using Vehictory.Api.Data;
using Vehictory.Api.DTOs;
using Vehictory.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Vehictory.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/vehicles")]
public class VehiclesController(VehictoryDbContext db) : ControllerBase
{
    // internal: ook gebruikt door de backfill in Program.cs voor bestaande, vóór deze
    // functionaliteit opgeslagen foto's.
    internal const int PhotoMaxDimension = 1600;
    internal const int PhotoThumbnailDimension = 320;

    // Toegankelijk voor de eigenaar én iedereen met wie het voertuig gedeeld is.
    private async Task<Vehicle?> GetAccessibleVehicle(int vehicleId, Guid userId) =>
        await db.Vehicles.SingleOrDefaultAsync(v =>
            v.Id == vehicleId && (v.UserId == userId || v.Shares.Any(s => s.UserId == userId)));

    // Alleen voor de eigenaar (bewerken/verwijderen/delen beheren).
    private async Task<Vehicle?> GetOwnedVehicle(int vehicleId, Guid userId) =>
        await db.Vehicles.SingleOrDefaultAsync(v => v.Id == vehicleId && v.UserId == userId);

    private static VehicleResponse ToResponse(Vehicle v, Guid userId) => new(
        v.Id, v.Naam, v.Merk, v.Type, v.Bouwjaar, v.Aankoopdatum,
        v.Aanschafprijs, v.Restwaarde, ToDto(v.Afschrijvingstabel), v.Verkoopdatum, v.Verkoopprijs,
        v.UserId == userId, v.User!.Name, AuthController.ToDataUrl("image/jpeg", v.FotoThumbnail));

    // Null-check: kolom is leeg bij voertuigen van vóór de afschrijvingstabel.
    private static List<AfschrijvingsStaffelDto> ToDto(List<AfschrijvingsStaffel>? tabel) =>
        (tabel ?? []).OrderBy(s => s.VanafLeeftijd).Select(s => new AfschrijvingsStaffelDto(s.VanafLeeftijd, s.PercentagePerMaand)).ToList();

    private static string? Validate(VehicleRequest request)
    {
        if (request.Aanschafprijs < 0 || request.Restwaarde < 0 || request.Verkoopprijs < 0)
            return "Bedragen kunnen niet negatief zijn.";
        if (request.Bouwjaar is { } bouwjaar && (bouwjaar < 1886 || bouwjaar > DateTime.Today.Year + 1))
            return "Vul een geldig bouwjaar in.";
        if (request.Bouwjaar > request.Aankoopdatum?.Year + 1)
            return "Het bouwjaar kan niet na de aankoopdatum liggen.";

        var tabel = request.Afschrijvingstabel ?? [];
        if (tabel.Count > 50) return "De afschrijvingstabel mag maximaal 50 regels bevatten.";
        if (tabel.Any(s => s.PercentagePerMaand is < 0 or >= 100))
            return "Afschrijvingspercentages moeten tussen 0 en 100 liggen.";
        if (tabel.Any(s => s.VanafLeeftijd < 0) || tabel.DistinctBy(s => s.VanafLeeftijd).Count() != tabel.Count)
            return "Elke leeftijd in de afschrijvingstabel mag maar één keer voorkomen en kan niet negatief zijn.";
        if (tabel.Count > 0 && tabel.Min(s => s.VanafLeeftijd) != 0)
            return "De afschrijvingstabel moet beginnen bij leeftijd 0.";
        // Afschrijving per leeftijd kan alleen berekend worden als bekend is hoe oud de auto is en sinds wanneer.
        if (tabel.Count > 0 && request.Aanschafprijs is not null)
        {
            if (request.Bouwjaar is null) return "Vul het bouwjaar in: de afschrijving hangt af van de leeftijd van de auto.";
            if (request.Aankoopdatum is null) return "Vul de aankoopdatum in: vanaf die datum wordt afgeschreven.";
        }
        if (request.Restwaarde > request.Aanschafprijs)
            return "De minimale restwaarde kan niet hoger zijn dan de aanschafprijs.";
        if (request.Verkoopprijs is not null && request.Verkoopdatum is null)
            return "Vul ook de verkoopdatum in.";
        if (request.Verkoopdatum < request.Aankoopdatum)
            return "De verkoopdatum kan niet vóór de aankoopdatum liggen.";
        return null;
    }

    private static void Apply(Vehicle vehicle, VehicleRequest request)
    {
        vehicle.Naam = request.Naam;
        vehicle.Merk = request.Merk;
        vehicle.Type = request.Type;
        vehicle.Bouwjaar = request.Bouwjaar;
        vehicle.Aankoopdatum = request.Aankoopdatum;
        vehicle.Aanschafprijs = request.Aanschafprijs;
        vehicle.Restwaarde = request.Restwaarde;
        vehicle.Afschrijvingstabel = (request.Afschrijvingstabel ?? [])
            .OrderBy(s => s.VanafLeeftijd)
            .Select(s => new AfschrijvingsStaffel { VanafLeeftijd = s.VanafLeeftijd, PercentagePerMaand = s.PercentagePerMaand })
            .ToList();
        vehicle.Verkoopdatum = request.Verkoopdatum;
        vehicle.Verkoopprijs = request.Verkoopprijs;
    }

    [HttpGet]
    public async Task<ActionResult<List<VehicleResponse>>> GetAll()
    {
        var userId = this.GetUserId();
        // Projectie op DB-niveau: de zware Foto-kolom wordt hier bewust niet opgehaald,
        // de lijst heeft alleen de kleine thumbnail nodig (zie /{id} voor de volledige foto).
        // AsNoTracking: de projectie bevat de (owned, jsonb) Afschrijvingstabel zonder het Vehicle zelf.
        var vehicles = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.UserId == userId || v.Shares.Any(s => s.UserId == userId))
            .OrderBy(v => v.Naam)
            .Select(v => new
            {
                v.Id, v.Naam, v.Merk, v.Type, v.Bouwjaar, v.Aankoopdatum,
                v.Aanschafprijs, v.Restwaarde, v.Afschrijvingstabel, v.Verkoopdatum, v.Verkoopprijs, v.UserId,
                EigenaarNaam = v.User!.Name, v.FotoThumbnail,
            })
            .ToListAsync();

        return Ok(vehicles.Select(v => new VehicleResponse(
            v.Id, v.Naam, v.Merk, v.Type, v.Bouwjaar, v.Aankoopdatum,
            v.Aanschafprijs, v.Restwaarde, ToDto(v.Afschrijvingstabel), v.Verkoopdatum, v.Verkoopprijs,
            v.UserId == userId, v.EigenaarNaam, AuthController.ToDataUrl("image/jpeg", v.FotoThumbnail))));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<VehicleResponse>> GetById(int id)
    {
        var userId = this.GetUserId();
        // Projectie op DB-niveau: de zware Foto-kolom wordt hier bewust niet opgehaald,
        // de detailpagina haalt de volledige foto lazy op via GET /{id}/photo.
        var vehicle = await db.Vehicles
            .AsNoTracking()
            .Where(v => v.Id == id && (v.UserId == userId || v.Shares.Any(s => s.UserId == userId)))
            .Select(v => new
            {
                v.Id, v.Naam, v.Merk, v.Type, v.Bouwjaar, v.Aankoopdatum,
                v.Aanschafprijs, v.Restwaarde, v.Afschrijvingstabel, v.Verkoopdatum, v.Verkoopprijs, v.UserId,
                EigenaarNaam = v.User!.Name, v.FotoThumbnail,
            })
            .SingleOrDefaultAsync();
        if (vehicle is null) return NotFound();

        return Ok(new VehicleResponse(
            vehicle.Id, vehicle.Naam, vehicle.Merk, vehicle.Type, vehicle.Bouwjaar, vehicle.Aankoopdatum,
            vehicle.Aanschafprijs, vehicle.Restwaarde, ToDto(vehicle.Afschrijvingstabel), vehicle.Verkoopdatum,
            vehicle.Verkoopprijs, vehicle.UserId == userId, vehicle.EigenaarNaam,
            AuthController.ToDataUrl("image/jpeg", vehicle.FotoThumbnail)));
    }

    // Aparte endpoint voor de volledige foto (i.p.v. inline base64 in GetById): voorkomt dat elke
    // detailpagina-load 850KB+ JSON meestuurt, en maakt echte HTTP-caching van de foto mogelijk.
    [HttpGet("{id:int}/photo")]
    public async Task<IActionResult> GetPhoto(int id)
    {
        var userId = this.GetUserId();
        var vehicle = await GetAccessibleVehicle(id, userId);
        if (vehicle?.Foto is null) return NotFound();

        return File(vehicle.Foto, vehicle.FotoContentType ?? "image/jpeg");
    }

    [HttpPost]
    public async Task<ActionResult<VehicleResponse>> Create(VehicleRequest request)
    {
        if (Validate(request) is { } error) return BadRequest(error);

        var userId = this.GetUserId();
        var vehicle = new Vehicle { UserId = userId, Naam = request.Naam };
        Apply(vehicle, request);

        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();
        await db.Entry(vehicle).Reference(v => v.User).LoadAsync();

        var response = ToResponse(vehicle, userId);
        return CreatedAtAction(nameof(GetById), new { id = vehicle.Id }, response);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, VehicleRequest request)
    {
        var userId = this.GetUserId();
        var vehicle = await GetOwnedVehicle(id, userId);
        if (vehicle is null) return NotFound();
        if (Validate(request) is { } error) return BadRequest(error);

        // Bij (gewijzigde) verkoop eindigen lopende vaste lasten op de verkoopdatum, net zoals een nieuwe
        // vaste last de lopende post van dezelfde soort beëindigt. Zo tellen er na verkoop geen termijnen meer mee.
        if (request.Verkoopdatum is { } verkoopdatum && verkoopdatum != vehicle.Verkoopdatum)
        {
            var lopend = await db.RecurringCosts
                .Where(r => r.VehicleId == id && r.Startdatum < verkoopdatum
                    && (r.Einddatum == null || r.Einddatum > verkoopdatum))
                .ToListAsync();
            foreach (var cost in lopend) cost.Einddatum = verkoopdatum;
        }

        Apply(vehicle, request);

        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = this.GetUserId();
        var vehicle = await GetOwnedVehicle(id, userId);
        if (vehicle is null) return NotFound();

        db.Vehicles.Remove(vehicle);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/photo")]
    public async Task<ActionResult<VehicleResponse>> UpdatePhoto(int id, IFormFile file)
    {
        var userId = this.GetUserId();
        var vehicle = await GetOwnedVehicle(id, userId);
        if (vehicle is null) return NotFound();

        var image = await AuthController.ReadImage(file, PhotoMaxDimension, PhotoThumbnailDimension);
        if (image.Error is not null) return BadRequest(image.Error);

        vehicle.Foto = image.Content;
        vehicle.FotoContentType = image.ContentType;
        vehicle.FotoThumbnail = image.Thumbnail;
        await db.SaveChangesAsync();
        await db.Entry(vehicle).Reference(v => v.User).LoadAsync();
        return Ok(ToResponse(vehicle, userId));
    }

    // --- Delen met een 2e account (bv. partner) ---
    // Alleen de eigenaar mag zien met wie het voertuig gedeeld is, en delen toevoegen/intrekken.
    // Wie het voertuig gedeeld heeft, kan het wel bekijken en tankbeurten/onderhoud toevoegen
    // (zie GetAccessibleVehicle in FuelEntriesController/MaintenanceEntriesController).

    [HttpGet("{id:int}/shares")]
    public async Task<ActionResult<List<VehicleShareResponse>>> GetShares(int id)
    {
        var userId = this.GetUserId();
        if (await GetOwnedVehicle(id, userId) is null) return NotFound();

        var shares = await db.VehicleShares
            .Include(s => s.User)
            .Where(s => s.VehicleId == id)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new VehicleShareResponse(s.UserId, s.User!.Email, s.User.Name, s.CreatedAt))
            .ToListAsync();

        return Ok(shares);
    }

    [HttpPost("{id:int}/shares")]
    public async Task<ActionResult<VehicleShareResponse>> AddShare(int id, ShareVehicleRequest request)
    {
        var userId = this.GetUserId();
        if (await GetOwnedVehicle(id, userId) is null) return NotFound();

        var email = request.Email.Trim().ToLowerInvariant();
        var target = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (target is null)
            return NotFound("Geen account gevonden met dit e-mailadres. Laat diegene eerst zelf een account aanmaken via de registratiepagina.");

        if (target.Id == userId)
            return BadRequest("Je kunt een voertuig niet met je eigen account delen.");

        if (await db.VehicleShares.AnyAsync(s => s.VehicleId == id && s.UserId == target.Id))
            return Conflict("Dit voertuig is al gedeeld met dit account.");

        var share = new VehicleShare { VehicleId = id, UserId = target.Id };
        db.VehicleShares.Add(share);
        await db.SaveChangesAsync();

        return Ok(new VehicleShareResponse(target.Id, target.Email, target.Name, share.CreatedAt));
    }

    [HttpDelete("{id:int}/shares/{sharedUserId:guid}")]
    public async Task<IActionResult> RemoveShare(int id, Guid sharedUserId)
    {
        var userId = this.GetUserId();
        if (await GetOwnedVehicle(id, userId) is null) return NotFound();

        var share = await db.VehicleShares.SingleOrDefaultAsync(s => s.VehicleId == id && s.UserId == sharedUserId);
        if (share is null) return NotFound();

        db.VehicleShares.Remove(share);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
