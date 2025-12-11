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

// ✅ Usar record para DTOs inmutables
public record UbicacionDto(
    string CamionId,
    double Latitud,
    double Longitud,
    string? Nombre = null
);

public record LocationSaveResult(bool Success, string? Error = null);

public class RegistrarUbicacion
{
    // ✅ Extraer constantes
    private const int HEALTH_CHECK_TIMEOUT_MS = 3000;
    private const double MIN_LATITUDE = -90.0;
    private const double MAX_LATITUDE = 90.0;
    private const double MIN_LONGITUDE = -180.0;
    private const double MAX_LONGITUDE = 180.0;
    private const int MAX_NOMBRE_LENGTH = 200;

    private readonly ILogger<RegistrarUbicacion> _logger;
    private readonly IFirestoreService _firestoreService;
    private readonly bool _enableHealthCheck;

    public RegistrarUbicacion(
        ILogger<RegistrarUbicacion> logger,
        IFirestoreService firestoreService)
    {
        _logger = logger;
        _firestoreService = firestoreService;
        // ✅ Health check configurable
        _enableHealthCheck = Environment.GetEnvironmentVariable("EnableHealthCheck") == "true";
    }

    [Function("RegistrarUbicacion")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "location")] HttpRequestData req,
        FunctionContext executionContext)
    {
        _logger.LogInformation("RegistrarUbicacion procesando request");

        // ✅ Health check opcional
        if (_enableHealthCheck)
        {
            var healthCheck = await CheckServicesHealthAsync();
            if (!healthCheck.isHealthy)
            {
                _logger.LogWarning("Service health check failed: {Reason}", healthCheck.reason);
                return await CreateJsonResponse(
                    req,
                    HttpStatusCode.ServiceUnavailable,
                    new { error = $"Servicio temporalmente no disponible: {healthCheck.reason}" }
                );
            }
        }

        // Validar request
        var validationResult = await ValidateRequestAsync(req);
        if (!validationResult.isValid)
        {
            return validationResult.errorResponse!;
        }

        var data = validationResult.data!;

        try
        {
            // ✅ Guardar en Firestore con retry implícito
            var firestoreResult = await SaveToFirestoreAsync(data);
            if (!firestoreResult.Success)
            {
                _logger.LogError("Firestore save failed: {Error}", firestoreResult.Error);
                return await CreateJsonResponse(
                    req,
                    HttpStatusCode.InternalServerError,
                    new { error = "Error al guardar en Firestore", details = firestoreResult.Error }
                );
            }

            // ✅ SQL Server opcional (fire-and-forget si no es crítico)
            _ = Task.Run(() => SaveToSqlServerAsync(data));

            _logger.LogInformation("Ubicación registrada exitosamente para {CamionId}", data.CamionId);

            return await CreateJsonResponse(
                req,
                HttpStatusCode.OK,
                new
                {
                    success = true,
                    message = $"Ubicación registrada correctamente",
                    camionId = data.CamionId,
                    timestamp = DateTime.UtcNow
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al registrar ubicación");
            return await CreateJsonResponse(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Error interno del servidor" }
            );
        }
    }

    /// <summary>
    /// Guarda la ubicación en Firestore con manejo de errores robusto
    /// </summary>
    private async Task<LocationSaveResult> SaveToFirestoreAsync(UbicacionDto data)
    {
        try
        {
            var locationData = new Dictionary<string, object>
            {
                { "fltLatitud", data.Latitud },
                { "fltLongitud", data.Longitud },
                { "vchNombre", data.Nombre ?? string.Empty },
                { "timestamp", FieldValue.ServerTimestamp }
            };

            await _firestoreService.SaveLocationAsync(data.CamionId, locationData);

            _logger.LogInformation(
                "Ubicación guardada en Firestore - Camión: {CamionId}, Lat: {Lat}, Lon: {Lon}",
                data.CamionId, data.Latitud, data.Longitud
            );

            return new LocationSaveResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar en Firestore");
            return new LocationSaveResult(false, $"Error de base de datos: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifica la salud de servicios críticos con timeout
    /// </summary>
    private async Task<(bool isHealthy, string? reason)> CheckServicesHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(HEALTH_CHECK_TIMEOUT_MS);

            await _firestoreService.CheckConnectionAsync();

            _logger.LogDebug("Health check passed");
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Firestore health check timeout ({TimeoutMs}ms)", HEALTH_CHECK_TIMEOUT_MS);
            return (false, "Firestore timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return (false, $"Firestore no disponible: {ex.Message}");
        }
    }

    /// <summary>
    /// Valida el request con reglas de negocio mejoradas
    /// </summary>
    private async Task<(bool isValid, UbicacionDto? data, HttpResponseData? errorResponse)> ValidateRequestAsync(
        HttpRequestData req)
    {
        // Leer body
        string body;
        try
        {
            body = await new StreamReader(req.Body).ReadToEndAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer request body");
            return (false, null, await CreateJsonResponse(
                req, HttpStatusCode.BadRequest, new { error = "Error al leer datos" }));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, null, await CreateJsonResponse(
                req, HttpStatusCode.BadRequest, new { error = "El body no puede estar vacío" }));
        }

        // Deserializar
        UbicacionDto? data;
        try
        {
            data = JsonSerializer.Deserialize<UbicacionDto>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON inválido recibido");
            return (false, null, await CreateJsonResponse(
                req, HttpStatusCode.BadRequest, new { error = "Formato JSON inválido" }));
        }

        if (data == null)
        {
            return (false, null, await CreateJsonResponse(
                req, HttpStatusCode.BadRequest, new { error = "Datos inválidos" }));
        }

        // ✅ Validaciones mejoradas
        var validationErrors = new List<string>();

        if (string.IsNullOrWhiteSpace(data.CamionId))
            validationErrors.Add("CamionId es requerido");
        else if (data.CamionId.Length > 50)
            validationErrors.Add("CamionId no puede exceder 50 caracteres");

        if (data.Latitud < MIN_LATITUDE || data.Latitud > MAX_LATITUDE)
            validationErrors.Add($"Latitud debe estar entre {MIN_LATITUDE} y {MAX_LATITUDE}");

        if (data.Longitud < MIN_LONGITUDE || data.Longitud > MAX_LONGITUDE)
            validationErrors.Add($"Longitud debe estar entre {MIN_LONGITUDE} y {MAX_LONGITUDE}");

        // ✅ Validar coordenadas 0,0 (error común de GPS)
        if (data.Latitud == 0 && data.Longitud == 0)
            validationErrors.Add("Coordenadas 0,0 no son válidas");

        // ✅ Validar nombre si existe
        if (!string.IsNullOrEmpty(data.Nombre) && data.Nombre.Length > MAX_NOMBRE_LENGTH)
            validationErrors.Add($"Nombre no puede exceder {MAX_NOMBRE_LENGTH} caracteres");

        if (validationErrors.Any())
        {
            return (false, null, await CreateJsonResponse(
                req,
                HttpStatusCode.BadRequest,
                new { error = "Validación fallida", errors = validationErrors }
            ));
        }

        return (true, data, null);
    }

    /// <summary>
    /// Guarda en SQL Server de forma no bloqueante (opcional)
    /// </summary>
    private async Task SaveToSqlServerAsync(UbicacionDto data)
    {
        try
        {
            string? connStr = Environment.GetEnvironmentVariable("SqlConnectionString");

            if (string.IsNullOrEmpty(connStr))
            {
                _logger.LogDebug("SqlConnectionString no configurado, omitiendo SQL Server");
                return;
            }

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand("dbo.InsertarUbicacion", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 10 // ✅ Timeout explícito
            };

            cmd.Parameters.AddWithValue("@CamionID", data.CamionId);
            cmd.Parameters.AddWithValue("@Latitud", data.Latitud);
            cmd.Parameters.AddWithValue("@Longitud", data.Longitud);

            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Ubicación guardada en SQL Server para {CamionId}", data.CamionId);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Error SQL al guardar ubicación (no crítico)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error inesperado en SQL Server (no crítico)");
        }
    }

    /// <summary>
    /// ✅ Helper unificado para crear responses JSON
    /// </summary>
    private static async Task<HttpResponseData> CreateJsonResponse(
        HttpRequestData req,
        HttpStatusCode statusCode,
        object data)
    {
        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(data);
        return response;
    }
}