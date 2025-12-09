using FunctionAppRegUbicacion.Services;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging;

public class FirestoreService : IFirestoreService
{ 
    private readonly FirestoreDb _db;
    private readonly ILogger<FirestoreService> _logger;

    public FirestoreService(FirestoreDb firestoreDb, ILogger<FirestoreService> logger)
    {
        _db = firestoreDb;
        _logger = logger;
    }

    public async Task<DocumentReference> SaveLocationAsync(string camionId, Dictionary<string, object> locationData)
    {
        try
        {
            DocumentReference docRef = _db.Collection("locations").Document(camionId);
            await docRef.SetAsync(locationData, SetOptions.MergeAll);

            _logger.LogInformation($"Ubicación guardada en Firestore para camión {camionId}");
            return docRef;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error al guardar en Firestore: {ex.Message}");
            throw;
        }
    }
    /// <summary>
    /// Verifica la conectividad realizando una operación simple en Firestore
    /// </summary>
    public async Task CheckConnectionAsync()
    {
        try
        {
            // Intenta leer una colección (sin importar si existe)
            // Esto forzará una conexión a Firestore para verificar que funciona
            var collection = _db.Collection("health-check");
            var query = collection.Limit(1);

            await query.GetSnapshotAsync();

            _logger.LogInformation("Firestore connection verified successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify Firestore connection");
            throw;
        }
    }
}