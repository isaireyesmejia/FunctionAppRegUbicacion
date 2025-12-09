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
}