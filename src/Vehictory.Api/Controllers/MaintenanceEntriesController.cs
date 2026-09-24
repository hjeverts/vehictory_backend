using Vehictory.Api.Data;
using Vehictory.Api.DTOs;
using Vehictory.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Vehictory.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/vehicles/{vehicleId:int}/maintenance")]
public class MaintenanceEntriesController(VehictoryDbContext db) : ControllerBase
{
    // Facturen/bonnetjes als PDF kunnen groter zijn dan foto's, vandaar een ruimere cap dan
    // de 2 MB voor voertuig-/profielfoto's.
    private const long MaxAttachmentSize = 10 * 1024 * 1024;
    private const int AttachmentPhotoMaxDimension = 1600;
    private const int AttachmentThumbnailDimension = 320;

    // Eigenaar én iedereen met wie het voertuig gedeeld is, mogen onderhoud lezen/toevoegen/verwijderen.
    private async Task<Vehicle?> GetAccessibleVehicle(int vehicleId)
    {
        var userId = this.GetUserId();
        return await db.Vehicles.SingleOrDefaultAsync(v =>
            v.Id == vehicleId && (v.UserId == userId || v.Shares.Any(s => s.UserId == userId)));
    }

    private static MaintenanceAttachmentResponse ToAttachmentResponse(
        int id, string fileName, string contentType, byte[]? thumbnail)
    {
        var isImage = contentType != "application/pdf";
        return new MaintenanceAttachmentResponse(
            id, fileName, contentType, isImage, isImage ? AuthController.ToDataUrl("image/jpeg", thumbnail) : null);
    }

    [HttpGet]
    public async Task<ActionResult<List<MaintenanceEntryResponse>>> GetAll(int vehicleId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        // Projectie op DB-niveau: de zware Content-kolom van bijlagen wordt hier bewust niet
        // opgehaald, alleen metadata + thumbnail (zie GetAttachment voor de volledige inhoud).
        var rows = await db.MaintenanceEntries
            .Where(m => m.VehicleId == vehicleId)
            .OrderByDescending(m => m.Datum)
            .Select(m => new
            {
                m.Id, m.VehicleId, m.Datum, m.Odometer, m.MaintenanceTypeId,
                MaintenanceTypeNaam = m.MaintenanceType!.Naam, m.Notitie, m.Kosten,
                Attachments = m.Attachments
                    .OrderBy(a => a.CreatedAt)
                    .Select(a => new { a.Id, a.FileName, a.ContentType, a.Thumbnail })
                    .ToList(),
            })
            .ToListAsync();

        return Ok(rows.Select(m => new MaintenanceEntryResponse(
            m.Id, m.VehicleId, m.Datum, m.Odometer, m.MaintenanceTypeId, m.MaintenanceTypeNaam, m.Notitie, m.Kosten,
            m.Attachments.Select(a => ToAttachmentResponse(a.Id, a.FileName, a.ContentType, a.Thumbnail)).ToList())));
    }

    [HttpPost]
    public async Task<ActionResult<MaintenanceEntryResponse>> Create(int vehicleId, MaintenanceEntryRequest request)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        if (request.Kosten < 0) return BadRequest("Kosten kunnen niet negatief zijn.");

        var type = await db.MaintenanceTypes.FindAsync(request.MaintenanceTypeId);
        if (type is null) return BadRequest("Onbekend onderhoudstype.");

        var entry = new MaintenanceEntry
        {
            VehicleId = vehicleId,
            Datum = request.Datum,
            Odometer = request.Odometer,
            MaintenanceTypeId = request.MaintenanceTypeId,
            Notitie = request.Notitie,
            Kosten = request.Kosten,
        };

        db.MaintenanceEntries.Add(entry);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { vehicleId },
            new MaintenanceEntryResponse(
                entry.Id, entry.VehicleId, entry.Datum, entry.Odometer, entry.MaintenanceTypeId, type.Naam,
                entry.Notitie, entry.Kosten, []));
    }

    // Wijzigt alleen de velden van de onderhoudsregel; bijlagen staan in een eigen tabel en blijven
    // ongemoeid (die verwijder je expliciet via DeleteAttachment).
    [HttpPut("{id:int}")]
    public async Task<ActionResult<MaintenanceEntryResponse>> Update(int vehicleId, int id, MaintenanceEntryRequest request)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();
        if (request.Kosten < 0) return BadRequest("Kosten kunnen niet negatief zijn.");

        var entry = await db.MaintenanceEntries.SingleOrDefaultAsync(m => m.Id == id && m.VehicleId == vehicleId);
        if (entry is null) return NotFound();

        var type = await db.MaintenanceTypes.FindAsync(request.MaintenanceTypeId);
        if (type is null) return BadRequest("Onbekend onderhoudstype.");

        entry.Datum = request.Datum;
        entry.Odometer = request.Odometer;
        entry.MaintenanceTypeId = request.MaintenanceTypeId;
        entry.Notitie = request.Notitie;
        entry.Kosten = request.Kosten;
        await db.SaveChangesAsync();

        var attachments = await db.MaintenanceAttachments
            .Where(a => a.MaintenanceEntryId == id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.FileName, a.ContentType, a.Thumbnail })
            .ToListAsync();

        return Ok(new MaintenanceEntryResponse(
            entry.Id, entry.VehicleId, entry.Datum, entry.Odometer, entry.MaintenanceTypeId, type.Naam,
            entry.Notitie, entry.Kosten,
            attachments.Select(a => ToAttachmentResponse(a.Id, a.FileName, a.ContentType, a.Thumbnail)).ToList()));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int vehicleId, int id)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var entry = await db.MaintenanceEntries.SingleOrDefaultAsync(m => m.Id == id && m.VehicleId == vehicleId);
        if (entry is null) return NotFound();

        db.MaintenanceEntries.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:int}/attachments")]
    public async Task<ActionResult<MaintenanceAttachmentResponse>> AddAttachment(int vehicleId, int id, IFormFile file)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();
        var entryExists = await db.MaintenanceEntries.AnyAsync(m => m.Id == id && m.VehicleId == vehicleId);
        if (!entryExists) return NotFound();

        var (attachment, error) = await ReadAttachment(file);
        if (error is not null) return BadRequest(error);

        attachment!.MaintenanceEntryId = id;
        db.MaintenanceAttachments.Add(attachment);
        await db.SaveChangesAsync();

        return Ok(ToAttachmentResponse(attachment.Id, attachment.FileName, attachment.ContentType, attachment.Thumbnail));
    }

    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> GetAttachment(int vehicleId, int id, int attachmentId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var attachment = await db.MaintenanceAttachments
            .SingleOrDefaultAsync(a => a.Id == attachmentId && a.MaintenanceEntryId == id);
        if (attachment is null) return NotFound();

        var contentDisposition = new System.Net.Mime.ContentDisposition
        {
            FileName = attachment.FileName,
            Inline = true,
        };
        Response.Headers.ContentDisposition = contentDisposition.ToString();
        return File(attachment.Content, attachment.ContentType);
    }

    [HttpDelete("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> DeleteAttachment(int vehicleId, int id, int attachmentId)
    {
        if (await GetAccessibleVehicle(vehicleId) is null) return NotFound();

        var attachment = await db.MaintenanceAttachments
            .SingleOrDefaultAsync(a => a.Id == attachmentId && a.MaintenanceEntryId == id);
        if (attachment is null) return NotFound();

        db.MaintenanceAttachments.Remove(attachment);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Accepteert JPEG/PNG/WebP (wordt net als voertuigfoto's verkleind + hergecomprimeerd, met
    // thumbnail) of PDF (ongewijzigd opgeslagen, geen thumbnail).
    private static async Task<(MaintenanceAttachment? Attachment, string? Error)> ReadAttachment(IFormFile? file)
    {
        if (file is null || file.Length == 0) return (null, "Selecteer een bestand.");
        if (file.Length > MaxAttachmentSize) return (null, "Het bestand mag maximaal 10 MB groot zijn.");

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var raw = memory.ToArray();

        if (raw is [0x25, 0x50, 0x44, 0x46, ..]) // %PDF
        {
            return (new MaintenanceAttachment
            {
                MaintenanceEntryId = 0,
                FileName = file.FileName,
                ContentType = "application/pdf",
                Content = raw,
            }, null);
        }

        var isImage = raw switch
        {
            [0xFF, 0xD8, ..] => true,
            [0x89, 0x50, 0x4E, 0x47, ..] => true,
            [0x52, 0x49, 0x46, 0x46, ..] when raw.Length >= 12
                && raw.AsSpan(8, 4).SequenceEqual("WEBP"u8) => true,
            _ => false,
        };
        if (!isImage) return (null, "Gebruik een JPEG-, PNG-, WebP-afbeelding of een PDF.");

        try
        {
            var (content, contentType, thumbnail) = AuthController.ProcessImage(
                raw, AttachmentPhotoMaxDimension, AttachmentThumbnailDimension);
            return (new MaintenanceAttachment
            {
                MaintenanceEntryId = 0,
                FileName = file.FileName,
                ContentType = contentType,
                Content = content,
                Thumbnail = thumbnail,
            }, null);
        }
        catch (SixLabors.ImageSharp.ImageFormatException)
        {
            return (null, "Gebruik een JPEG-, PNG-, WebP-afbeelding of een PDF.");
        }
    }
}
