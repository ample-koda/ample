namespace Ample.Storage.Takes;

/// <summary>A reference from a take to a stored data blob.</summary>
/// <param name="Role">What the blob is (for example <c>audio</c> or <c>melody</c>).</param>
/// <param name="DataId">Content address.</param>
public sealed record TakeDataRef(string Role, string DataId);

/// <summary>A stored take.</summary>
/// <param name="TakeId">Take id.</param>
/// <param name="PlanId">The plan it was made from (never missing).</param>
/// <param name="Created">When the take was stored.</param>
/// <param name="Data">The data blobs.</param>
/// <param name="MetaJson">Free metadata as JSON text.</param>
/// <param name="Tombstoned">True when the take was dropped (the record stays).</param>
/// <param name="TombstoneReason">Why it was dropped.</param>
public sealed record TakeRecord(string TakeId, string PlanId, DateTimeOffset Created, IReadOnlyList<TakeDataRef> Data, string MetaJson, bool Tombstoned, string? TombstoneReason);

/// <summary>A stored plan.</summary>
/// <param name="PlanId">Plan id.</param>
/// <param name="CanonicalJson">The frozen canonical text.</param>
/// <param name="Plan">The parsed plan.</param>
public sealed record StoredPlan(string PlanId, string CanonicalJson, PlanRecord Plan);

/// <summary>One render attempt of a plan: started, then completed, failed or cancelled. An attempt that stays <c>started</c> was interrupted.</summary>
/// <param name="AttemptId">Attempt id.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="Started">When it started.</param>
/// <param name="Outcome"><c>started</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Reason">Why it failed or was cancelled.</param>
/// <param name="TakeId">The take it produced, if completed.</param>
public sealed record AttemptRecord(Guid AttemptId, string PlanId, DateTimeOffset Started, string Outcome, string? Reason, string? TakeId);

/// <summary>A problem found while reading or verifying a take's data.</summary>
/// <param name="Kind"><c>missing</c>, <c>size</c> or <c>hash</c>.</param>
/// <param name="DataId">The blob.</param>
/// <param name="Detail">Human text.</param>
public sealed record DataProblem(string Kind, string DataId, string Detail);

/// <summary>A take read together with its plan. When data files are missing, <see cref="Missing"/> says which, and the plan tells how the take can be re-rendered or where to restore it from a backup.</summary>
/// <param name="Take">The take.</param>
/// <param name="Plan">Its plan.</param>
/// <param name="Missing">Data blobs that are not on disk.</param>
public sealed record TakeRead(TakeRecord Take, StoredPlan Plan, IReadOnlyList<DataProblem> Missing);

/// <summary>A blob that no take references.</summary>
/// <param name="DataId">Content address.</param>
/// <param name="InDatabase">False when the file exists on disk but has no database row (an interrupted put).</param>
/// <param name="Quarantined">True when it was already quarantined.</param>
public sealed record Orphan(string DataId, bool InDatabase, bool Quarantined);
