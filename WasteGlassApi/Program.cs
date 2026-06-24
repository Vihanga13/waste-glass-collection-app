using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

var builder = WebApplication.CreateBuilder(args);

// Render requires binding to the port it provides
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---- Firebase setup ----
string projectId = "waste-glass-app";

// Try environment variable first (used on Render), fall back to local file (used on your own laptop)
string? credentialsJson = Environment.GetEnvironmentVariable("FIREBASE_CREDENTIALS_JSON");

if (string.IsNullOrEmpty(credentialsJson))
{
    string keyPath = Path.Combine(AppContext.BaseDirectory, "firebase-key.json");
    credentialsJson = File.ReadAllText(keyPath);
}

FirebaseApp.Create(new AppOptions()
{
    Credential = GoogleCredential.FromJson(credentialsJson)
});

FirestoreDb db = new FirestoreDbBuilder
{
    ProjectId = projectId,
    JsonCredentials = credentialsJson
}.Build();

var app = builder.Build();

// Collector's starting location (fixed depot point)
double startLat = 6.9271;
double startLng = 79.8612;

// ---- Haversine distance function ----
static double Haversine(double lat1, double lng1, double lat2, double lng2)
{
    double R = 6371; // Earth's radius in km
    double dLat = (lat2 - lat1) * Math.PI / 180.0;
    double dLng = (lng2 - lng1) * Math.PI / 180.0;

    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
               Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
               Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

    double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    return R * c;
}

// ---- Test route ----
app.MapGet("/", () => "Hello World!");

// ---- Get all suppliers (raw, unsorted) ----
app.MapGet("/api/suppliers", async () =>
{
    var snapshot = await db.Collection("suppliers").GetSnapshotAsync();
    var suppliers = new List<Dictionary<string, object>>();

    foreach (var doc in snapshot.Documents)
    {
        var data = doc.ToDictionary();
        data["id"] = doc.Id;
        suppliers.Add(data);
    }

    return Results.Ok(suppliers);
});

// ---- Get optimal route (Dijkstra-style nearest-next ordering) ----
app.MapGet("/api/route", async () =>
{
    var snapshot = await db.Collection("suppliers").GetSnapshotAsync();

    var remaining = new List<Dictionary<string, object>>();
    foreach (var doc in snapshot.Documents)
    {
        var data = doc.ToDictionary();
        data["id"] = doc.Id;

        string status = data.ContainsKey("status") ? data["status"]?.ToString() ?? "Pending" : "Pending";

        if (status != "Collected")
        {
            remaining.Add(data);
        }
    }

    var orderedStops = new List<object>();
    double currentLat = startLat;
    double currentLng = startLng;
    double totalDistance = 0;
    bool isFirst = true;

    while (remaining.Count > 0)
    {
        double minDist = double.MaxValue;
        Dictionary<string, object>? nearest = null;

        foreach (var supplier in remaining)
        {
            double lat = Convert.ToDouble(supplier["lat"]);
            double lng = Convert.ToDouble(supplier["lng"]);
            double dist = Haversine(currentLat, currentLng, lat, lng);

            if (dist < minDist)
            {
                minDist = dist;
                nearest = supplier;
            }
        }

        if (nearest == null) break;

        totalDistance += minDist;

        orderedStops.Add(new
        {
            id = nearest["id"],
            name = nearest.ContainsKey("name") ? nearest["name"] : "Unknown",
            lat = nearest["lat"],
            lng = nearest["lng"],
            expectedKg = nearest.ContainsKey("expectedKg") ? nearest["expectedKg"] : 0,
            barcodeId = nearest.ContainsKey("barcodeId") ? nearest["barcodeId"] : "",
            status = isFirst ? "Next" : "Pending",
            distanceFromPrevious = Math.Round(minDist, 2)
        });

        currentLat = Convert.ToDouble(nearest["lat"]);
        currentLng = Convert.ToDouble(nearest["lng"]);
        remaining.Remove(nearest);
        isFirst = false;
    }

    return Results.Ok(new
    {
        totalDistanceKm = Math.Round(totalDistance, 2),
        remainingStops = orderedStops.Count,
        stops = orderedStops
    });
});

// ---- Submit a collection (after barcode scan + quantity entry) ----
app.MapPost("/api/collect", async (CollectRequest request) =>
{
    var snapshot = await db.Collection("suppliers")
        .WhereEqualTo("barcodeId", request.BarcodeId)
        .GetSnapshotAsync();

    if (snapshot.Documents.Count == 0)
    {
        return Results.NotFound(new { error = "No supplier found with that barcode ID." });
    }

    var doc = snapshot.Documents[0];

    var updates = new Dictionary<string, object>
    {
        { "status", "Collected" },
        { "clearKg", request.ClearKg },
        { "colouredKg", request.ColouredKg },
        { "condition", request.Condition },
        { "collectedAt", Timestamp.GetCurrentTimestamp() }
    };

    await doc.Reference.UpdateAsync(updates);

    return Results.Ok(new
    {
        message = "Collection recorded successfully.",
        supplierId = doc.Id,
        barcodeId = request.BarcodeId,
        status = "Collected"
    });
});

// ---- Trip summary (for Screen 3) ----
app.MapGet("/api/trip-summary", async () =>
{
    var snapshot = await db.Collection("suppliers").GetSnapshotAsync();

    var summaryList = new List<object>();
    double totalKg = 0;
    double totalDistance = 0;

    foreach (var doc in snapshot.Documents)
    {
        var data = doc.ToDictionary();

        string status = data.ContainsKey("status") ? data["status"]?.ToString() ?? "Pending" : "Pending";
        double expectedKg = data.ContainsKey("expectedKg") ? Convert.ToDouble(data["expectedKg"]) : 0;
        double clearKg = data.ContainsKey("clearKg") ? Convert.ToDouble(data["clearKg"]) : 0;
        double colouredKg = data.ContainsKey("colouredKg") ? Convert.ToDouble(data["colouredKg"]) : 0;
        double collectedKg = clearKg + colouredKg;

        bool isShortfall = status == "Collected" && collectedKg < expectedKg;

        totalKg += collectedKg;

        summaryList.Add(new
        {
            name = data.ContainsKey("name") ? data["name"] : "Unknown",
            barcodeId = data.ContainsKey("barcodeId") ? data["barcodeId"] : "",
            status,
            expectedKg,
            clearKg,
            colouredKg,
            collectedKg,
            condition = data.ContainsKey("condition") ? data["condition"] : null,
            shortfallWarning = isShortfall
        });
    }

    var allSuppliers = new List<Dictionary<string, object>>();
    foreach (var doc in snapshot.Documents)
    {
        var data = doc.ToDictionary();
        if (data.ContainsKey("lat") && data.ContainsKey("lng"))
        {
            allSuppliers.Add(data);
        }
    }

    double currentLat = startLat;
    double currentLng = startLng;
    var remainingForDistance = new List<Dictionary<string, object>>(allSuppliers);

    while (remainingForDistance.Count > 0)
    {
        double minDist = double.MaxValue;
        Dictionary<string, object>? nearest = null;

        foreach (var supplier in remainingForDistance)
        {
            double lat = Convert.ToDouble(supplier["lat"]);
            double lng = Convert.ToDouble(supplier["lng"]);
            double dist = Haversine(currentLat, currentLng, lat, lng);

            if (dist < minDist)
            {
                minDist = dist;
                nearest = supplier;
            }
        }

        if (nearest == null) break;

        totalDistance += minDist;
        currentLat = Convert.ToDouble(nearest["lat"]);
        currentLng = Convert.ToDouble(nearest["lng"]);
        remainingForDistance.Remove(nearest);
    }

    return Results.Ok(new
    {
        totalKgCollected = Math.Round(totalKg, 2),
        totalDistanceKm = Math.Round(totalDistance, 2),
        tripDurationMinutes = 90,
        suppliers = summaryList
    });
});

app.Run();

// ---- Request body shape for /api/collect ----
record CollectRequest(string BarcodeId, double ClearKg, double ColouredKg, string Condition);