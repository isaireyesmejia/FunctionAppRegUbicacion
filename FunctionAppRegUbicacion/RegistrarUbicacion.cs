using FunctionAppRegUbicacion.Services;
using Google.Cloud.Firestore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Net;
using System.Text.Json;

namespace FunctionAppRegUbicacion;

public class UbicacionDto
{
    public string CamionId { get; set; }
    public double Latitud { get; set; }
    public double Longitud { get; set; }
    public string Nombre { get; set; }
}

public class RegistrarUbicacion
{
    private readonly ILogger<RegistrarUbicacion> _logger;
    private readonly IFirestoreService _firestoreService;

    // Inyección de dependencias
    public RegistrarUbicacion(
        ILogger<RegistrarUbicacion> logger,
        IFirestoreService firestoreService)
    {
        _logger = logger;
        _firestoreService = firestoreService;
    }

    [Function("RegistrarUbicacion")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "location")] HttpRequestData req,
        FunctionContext executionContext)
    {
        _logger.LogInformation("RegistrarUbicacion processed a request.");

        // Deserializar y validar datos
        var (isValid, data, errorResponse) = await ValidateRequestAsync(req);
        if (!isValid)
        {
            return errorResponse;
        }

        try
        {
            _logger.LogInformation("Intentando guardar en Firestore");

            // Preparar datos para Firestore
            var locationData = new Dictionary<string, object>
            {
                { "fltLatitud", data.Latitud },
                { "fltLongitud", data.Longitud },
                { "vchNombre", data.Nombre ?? string.Empty },
                { "timestamp", FieldValue.ServerTimestamp }
            };

            // Guardar en Firestore usando el servicio inyectado
            await _firestoreService.SaveLocationAsync(data.CamionId, locationData);

            _logger.LogInformation($"Ubicación actualizada correctamente en Firestore para camión {data.CamionId}");

            // Guardar también en SQL Server (opcional)
            await SaveToSqlServerAsync(data);

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteStringAsync($"Ubicación registrada correctamente para camión {data.CamionId}");
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar ubicación");
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Error al registrar ubicación");
        }
    }

    private async Task<(bool isValid, UbicacionDto data, HttpResponseData errorResponse)> ValidateRequestAsync(HttpRequestData req)
    {
        string body = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrEmpty(body))
        {
            return (false, null, await CreateBadResponse(req, "El body de la petición no puede estar vacío"));
        }

        UbicacionDto data;
        try
        {
            data = JsonSerializer.Deserialize<UbicacionDto>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON inválido");
            return (false, null, await CreateBadResponse(req, "El formato JSON es inválido"));
        }

        if (data == null)
        {
            return (false, null, await CreateBadResponse(req, "Formato de datos inválido"));
        }

        if (string.IsNullOrEmpty(data.CamionId))
        {
            return (false, null, await CreateBadResponse(req, "CamionId es requerido"));
        }

        if (data.Latitud < -90 || data.Latitud > 90)
        {
            return (false, null, await CreateBadResponse(req, "Latitud debe estar entre -90 y 90"));
        }

        if (data.Longitud < -180 || data.Longitud > 180)
        {
            return (false, null, await CreateBadResponse(req, "Longitud debe estar entre -180 y 180"));
        }

        return (true, data, null);
    }

    private async Task<HttpResponseData> CreateBadResponse(HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        var errorJson = JsonSerializer.Serialize(new { error = message });
        await response.WriteStringAsync(errorJson);
        return response;
    }

    private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        var errorJson = JsonSerializer.Serialize(new { error = message });
        await response.WriteStringAsync(errorJson);
        return response;
    }

    private async Task SaveToSqlServerAsync(UbicacionDto data)
    {
        try
        {
            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString");

            if (string.IsNullOrEmpty(connStr))
            {
                _logger.LogWarning("SqlConnectionString no está configurado, omitiendo guardado en SQL Server");
                return;
            }

            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("dbo.InsertarUbicacion", conn);
            cmd.CommandType = CommandType.StoredProcedure;

            cmd.Parameters.AddWithValue("@CamionID", data.CamionId);
            cmd.Parameters.AddWithValue("@Latitud", data.Latitud);
            cmd.Parameters.AddWithValue("@Longitud", data.Longitud);

            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation($"Ubicación registrada en SQL Server para camión {data.CamionId}");
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Error SQL al guardar ubicación");
            throw;
        }
    }
}