using Microsoft.EntityFrameworkCore;

namespace memoana.Persistence;

public sealed class SqliteGameStateStore(IDbContextFactory<MemoAnaDbContext> factory) : IGameStateStore
{
    public IReadOnlyList<PersistedRoom> LoadRooms()
    {
        using var db = factory.CreateDbContext();
        return db.Rooms.AsNoTracking().ToArray();
    }

    public void Save(PersistedRoom room)
    {
        using var db = factory.CreateDbContext();
        var existing = db.Rooms.SingleOrDefault(x => x.RoomId == room.RoomId);
        if (existing is null) db.Rooms.Add(room);
        else
        {
            if (existing.Version != room.Version - 1 && room.Version > 1)
                throw new DbUpdateConcurrencyException($"Room '{room.RoomId}' has been changed by another operation.");
            db.Entry(existing).CurrentValues.SetValues(room);
        }
        db.SaveChanges();
    }
}
