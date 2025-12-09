using FunctionAppRegUbicacion.Services;
using Google.Cloud.Firestore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace FunctionAppRegUbicacion;

/// <summary>
/// Modelo de respuesta para el health check
/// </summary>
public class HealthCheckResponse
{
    public string Status { get; set; } // "Healthy", "Degraded", "Unhealthy"
    public DateTime Timestamp { get; set; }
    public Dictionary<string, ServiceHealthStatus> Services { get; set; }
    public TimeSpan TotalDuration { get; set; }
}

public class ServiceHealthStatus
{
    public bool IsHealthy { get; set; }
    public string Message { get; set; }
    public TimeSpan ResponseTime { get; set; }
}

/// <summary>
/// Función Azure para verificar el estado de salud de los servicios
/// </summary>
public class HealthCheck
{
    private readonly ILogger<HealthCheck> _logger;
    private readonly IFirestoreService _firestoreService;

    public HealthCheck(
        ILogger<HealthCheck> logger,
        IFirestoreService firestoreService)
    {
        _logger = logger;
        _firestoreService = firestoreService;
    }

    [Function("HealthCheck")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        FunctionContext executionContext)
    {
        _logger.LogInformation("Health check initiated");

        var startTime = DateTime.UtcNow;
        var healthStatus = new HealthCheckResponse
        {
            Timestamp = startTime,
            Services = new Dictionary<string, ServiceHealthStatus>()
        };

        // Verificar Firestore
        var firestoreHealth = await CheckFirestoreHealthAsync();
        healthStatus.Services["Firestore"] = firestoreHealth;

        // Verificar SQL Server
        var sqlHealth = await CheckSqlServerHealthAsync();
        healthStatus.Services["SqlServer"] = sqlHealth;

        // Calcular duración total
        healthStatus.TotalDuration = DateTime.UtcNow - startTime;

        // Determinar estado general
        healthStatus.Status = DetermineOverallStatus(healthStatus.Services);

        // Crear respuesta HTTP
        var statusCode = healthStatus.Status switch
        {
            "Healthy" => HttpStatusCode.OK,
            "Degraded" => HttpStatusCode.OK, // Aún funcional pero con advertencias
            "Unhealthy" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.InternalServerError
        };

        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await response.WriteStringAsync(JsonSerializer.Serialize(healthStatus, jsonOptions));

        _logger.LogInformation($"Health check completed: {healthStatus.Status}");

        return response;
    }

    /// <summary>
    /// Verifica la conectividad con Firestore
    /// </summary>
    private async Task<ServiceHealthStatus> CheckFirestoreHealthAsync()
    {
        var startTime = DateTime.UtcNow;

        try
        {
            _logger.LogInformation("Checking Firestore connectivity...");

            // Intenta obtener el cliente de Firestore
            await _firestoreService.CheckConnectionAsync();

            var responseTime = DateTime.UtcNow - startTime;

            // Si tarda más de 5 segundos, considéralo degradado
            if (responseTime.TotalSeconds > 5)
            {
                _logger.LogWarning($"Firestore response time is slow: {responseTime.TotalSeconds}s");
                return new ServiceHealthStatus
                {
                    IsHealthy = true,
                    Message = $"Connected but slow response ({responseTime.TotalMilliseconds:F0}ms)",
                    ResponseTime = responseTime
                };
            }

            return new ServiceHealthStatus
            {
                IsHealthy = true,
                Message = "Connected and responsive",
                ResponseTime = responseTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Firestore health check failed");

            return new ServiceHealthStatus
            {
                IsHealthy = false,
                Message = $"Connection failed: {ex.Message}",
                ResponseTime = DateTime.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Verifica la conectividad con SQL Server
    /// </summary>
    private async Task<ServiceHealthStatus> CheckSqlServerHealthAsync()
    {
        var startTime = DateTime.UtcNow;

        try
        {
            string connStr = Environment.GetEnvironmentVariable("SqlConnectionString");

            if (string.IsNullOrEmpty(connStr))
            {
                _logger.LogWarning("SqlConnectionString not configured");
                return new ServiceHealthStatus
                {
                    IsHealthy = true, // No es crítico
                    Message = "Not configured (optional service)",
                    ResponseTime = TimeSpan.Zero
                };
            }

            // Si deseas forzar un timeout de 5 segundos, modifica la cadena de conexión así:
            if (!string.IsNullOrEmpty(connStr) && !connStr.Contains("Connection Timeout=", StringComparison.OrdinalIgnoreCase))
            {
                connStr += (connStr.EndsWith(";") ? "" : ";") + "Connection Timeout=5;";
            }

            _logger.LogInformation("Checking SQL Server connectivity...");

            using var conn = new SqlConnection(connStr);

            await conn.OpenAsync();

            // Ejecuta una query simple para verificar conectividad
            using var cmd = new SqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();

            var responseTime = DateTime.UtcNow - startTime;

            if (responseTime.TotalSeconds > 3)
            {
                _logger.LogWarning($"SQL Server response time is slow: {responseTime.TotalSeconds}s");
                return new ServiceHealthStatus
                {
                    IsHealthy = true,
                    Message = $"Connected but slow response ({responseTime.TotalMilliseconds:F0}ms)",
                    ResponseTime = responseTime
                };
            }

            return new ServiceHealthStatus
            {
                IsHealthy = true,
                Message = "Connected and responsive",
                ResponseTime = responseTime
            };
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "SQL Server health check failed");

            return new ServiceHealthStatus
            {
                IsHealthy = false,
                Message = $"Connection failed: {ex.Message}",
                ResponseTime = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in SQL Server health check");

            return new ServiceHealthStatus
            {
                IsHealthy = false,
                Message = $"Unexpected error: {ex.Message}",
                ResponseTime = DateTime.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Determina el estado general basado en los servicios individuales
    /// </summary>
    private string DetermineOverallStatus(Dictionary<string, ServiceHealthStatus> services)
    {
        // Firestore es crítico
        if (!services["Firestore"].IsHealthy)
        {
            return "Unhealthy";
        }

        // SQL Server es opcional, pero si está configurado y falla, es degradado
        if (services.ContainsKey("SqlServer") &&
            !services["SqlServer"].IsHealthy &&
            services["SqlServer"].Message != "Not configured (optional service)")
        {
            return "Degraded";
        }

        // Si algún servicio tiene respuesta lenta
        if (services.Any(s => s.Value.Message.Contains("slow")))
        {
            return "Degraded";
        }

        return "Healthy";
    }
}
