using Google.Cloud.Firestore;

namespace FunctionAppRegUbicacion.Services;

public interface IFirestoreService
{
    Task<DocumentReference> SaveLocationAsync(string camionId, Dictionary<string, object> locationData);
    /// <summary>
    /// Verifica la conectividad con Firestore
    /// </summary>
    Task CheckConnectionAsync();
}