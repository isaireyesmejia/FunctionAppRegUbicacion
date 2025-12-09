using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FunctionAppRegUbicacion.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Registrar FirestoreDb como Singleton
builder.Services.AddSingleton<FirestoreDb>(provider =>
{
    string projectId = Environment.GetEnvironmentVariable("FirestoreProjectId");

    if (string.IsNullOrEmpty(projectId))
    {
        throw new InvalidOperationException("FirestoreProjectId no está configurado");
    }

    // Obtener credenciales
    string json = GetFirebaseCredentials().Result;

    if (string.IsNullOrEmpty(json))
    {
        throw new InvalidOperationException("No se pudieron obtener las credenciales de Firebase");
    }

    var credential = GoogleCredential.FromJson(json);
    var firestoreClientBuilder = new FirestoreClientBuilder
    {
        Credential = credential
    };

    return FirestoreDb.CreateAsync(projectId, firestoreClientBuilder.Build()).Result;
});

// Registrar el servicio de Firestore
builder.Services.AddScoped<IFirestoreService, FirestoreService>();

// Registrar otros servicios si los tienes
// builder.Services.AddScoped<ISqlLocationRepository, SqlLocationRepository>();

builder.Build().Run();

static async Task<string> GetFirebaseCredentials()
{
    // Primero intentar Key Vault (más seguro)
    string keyVaultUrl = Environment.GetEnvironmentVariable("KeyVaultUrl");

    if (!string.IsNullOrEmpty(keyVaultUrl))
    {
        try
        {
            var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
            KeyVaultSecret secret = await client.GetSecretAsync("googlellave39");
            return secret.Value;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error al obtener credenciales de Key Vault: {ex.Message}");
        }
    }

    // Fallback a variable de entorno (solo para desarrollo local)
    return Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_CONTENT");
}