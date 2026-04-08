# Performance Research #2: Pathfinding And Movement

Scope: `Lopital.WalkComponent`, `GLib.PathfinderJob`, `GLib.Pathfinder`, `Lopital.Floor`, `GLib.GridMap`.
Source set reviewed: `docs/performance-investigation.md`, `src/*.cs`, and `Assembly-CSharp.dll` via `ilspycmd`.

## Route lifecycle

The vanilla movement flow is split into three phases:

1. `SetDestination(...)` establishes the target and resets walk state.
2. `UpdateDestinationSet()` creates a `PathfinderJob` and switches to `LookingForPath`.
3. `UpdateLookingForPath()` polls the job, applies the route on the main thread, or falls back to a broader search.

Concrete behavior observed in `WalkComponent`:

- `SetDestination(Vector2i, int, MovementType)` and `SetDestination(Vector2f, int, MovementType)` clear the current walk target, reset `m_walkMidpoint1`, zero `m_blockedCount`, call `CheckElevator()`, and then either snap to `Idle` if the target tile is already current or move to `DestinationSet` and request a path.
- `UpdateDestinationSet()` always calls `SetupJob(false, false, false)` and then switches to `LookingForPath`.
- `SetupJob(bool ignoreObjects, bool ignoreLookAhead, bool ignoreAccessRights)` constructs a new `PathfinderJob` every time it is called. The start tile depends on whether the character is sitting on a moving object; the end tile is either the final destination or the first walk midpoint.
- `UpdateLookingForPath()` starts the job if needed, waits for completion, then applies the result. Route adoption, sit/stand transitions, and `WalkState` changes all happen on the main thread.
- `PathfinderJob.ThreadFunction()` calls `Pathfinder.FindRoute(m_end, m_start, ...)`. `PathfinderRoute` stores the dequeued goal node and its parent chain, so the swapped arguments are intentional: the resulting `Nodes` list is in walk order for `WalkComponent`.

The route is not just a list of tiles. The walk state also carries:

- `m_walkMidpoint1` and `m_walkMidpoint2` for floor transitions.
- `m_objectSittingOn` and `m_objectToSitOn` for stretcher/chair/wheelchair interactions.
- `m_routeIndex`, `m_nextPosition`, `m_currentPosition`, `m_destinationFloor`.
- `m_blockedCount`, which is used to invalidate a route when the live path becomes inaccessible.

## What the pathfinder actually depends on

`Pathfinder.FindRoute(Vector2i start, Vector2i end, NavigationInfoProvider provider, int stepLimit, int accessRightsLevel, int preferredAccessRightsLevel, int lookAheadDistance, PathfinderFlags flags)` is a vanilla A* search with a fixed `stepLimit` and a per-run cost grid.

Important dependencies from `Floor` and `GridMap`:

- `Floor.IsAccessible(Vector2i currentPosition, Vector2i nextPosition, Vector2i startPosition, Vector2i targetPosition, int accessRightsLevel, bool ignoreObjects, bool ignoreAccessRights = false)` checks:
  - map bounds
  - floor existence
  - room access rights
  - logistics access rights
  - walls and door passability
  - center objects blocking movement
  - occupancy by active `ProcedureScript`
  - diagonal corner clearance
- `Floor.GetMovementCost(Vector2i, int, bool)` adds cost for:
  - object count on the tile
  - tile occupation
  - outdoor preference
  - room/logistics access rights relative to the preferred access level
- `Floor.GetMovementCost(Vector2i, Direction, bool)` is the look-ahead variant used by the pathfinder when `lookAheadDistance > 1`.
- `Floor.IsThroughDoor(...)` and `Floor.OpenDoor(...)` are used by `WalkComponent` during movement, not by the A* search itself.
- `GridMap.GetWaypoints(int floorFrom, Vector2i from, int floorTo, Vector2i to, AccessRights accessRights)` computes elevator/stair transitions from the floor graphs.
- `GridMap.Recalculate(...)` rebuilds the floor graph from tile walls, tiles, access rights, room access rights, and elevator tiles.

That means a route is sensitive to live state, not just coordinates.

## Elevator and floor-transition behavior

Floor transitions are a separate lifecycle from same-floor walking:

- `CheckElevator()` calls `GridMap.GetWaypoints(...)` with `AccessRights.STAFF` and writes the first two midpoints into `m_walkMidpoint1` / `m_walkMidpoint2`.
- `MoveFloor(bool fadeIn)` consumes those midpoints, switches `Floor`, and moves attached moving objects when the floor changes.
- If `m_walkMidpoint1 == null` but `m_walkMidpoint2 != null`, `MoveFloor()` treats the character as stuck in an elevator and cancels movement.

This is the highest-risk area for route caching. A cache keyed only by start/end tiles will miss:

- elevator availability
- stair/elevator graph recalculation
- floor-specific access rules
- mid-route floor transitions

## Repeated route requests

The mod already has a narrow dedupe guard:

- `PerformanceOptimizations.RouteRequestThrottleVector2iPatch`
- `PerformanceOptimizations.RouteRequestThrottleVector2fPatch`

Both target `WalkComponent.SetDestination(...)` and skip repeated requests for the same character, destination, floor, and movement type for a short TTL.

This is the right class of optimization because it prevents repeated job creation without changing the route semantics. It is still only a dedupe, not a route cache.

Repeated requests can still come from:

- AI state machines reasserting the same destination every frame
- fallback retries after `NO_ROUTE`
- sit/stand transitions that restart the walk job
- attached-character handling during stretcher transport

## Cache risk matrix

### High-risk invalidation sources

Any route cache must be invalidated or revalidated on:

- floor changes
- `WalkState` changes
- `AccessRights` or preferred access rights changes
- restricted-room changes
- elevator/stair graph changes
- door state changes
- dynamic object occupancy
- procedure-script occupancy
- stretcher or wheelchair attachment changes
- a different `lookAheadDistance`
- `MovementType` changes

### Why this is fragile

- `Floor.IsAccessible(...)` is not a pure geometric test. It checks permissions, occupancy, doors, and blocking objects.
- `Floor.GetMovementCost(...)` changes when objects move, users occupy tiles, or room/logistics access rights change.
- `WalkComponent.UpdateMovement(...)` can invalidate a route mid-run if a later segment becomes inaccessible.
- `WalkComponent.CheckElevator()` and `GridMap.GetWaypoints(...)` depend on elevator placement and floor graph state.
- `WalkComponent.FindAttachedCharacter()` and `MoveAttachedMovingObjects()` mean the character state is coupled to another entity while moving.

The safe conclusion is that route caching by endpoint alone is not viable.

## Safe optimizations

These are the changes that look safe after the current read:

- Keep the job on the existing pathfinding thread and keep result application on the main thread.
- Deduplicate repeated `SetDestination(...)` calls for the same character and same effective request, which is already in the mod.
- Profile route churn separately from route solve time. The main hot spot may be request volume, not `FindRoute(...)` itself.
- If a cache is added, make it short-lived, per-character, and keyed by the full effective request:
  - start tile
  - destination tile
  - floor
  - movement type
  - access rights
  - preferred access rights
  - look-ahead distance
  - ignore-objects / ignore-access-rights mode
  - stretcher or attachment state
- Revalidate cached hits against the current floor/access/elevator state before using them.

## Dangerous optimizations

Avoid these unless profiling proves the exact invalidation model is complete:

- caching by destination only
- caching across floor transitions
- caching across different access-rights levels
- caching routes that ignore dynamic objects and then reusing them for live movement
- moving `UpdateLookingForPath()` result application to a worker thread
- sharing one route object between multiple characters
- treating elevator paths as static when `GridMap.Recalculate(...)` may have changed the graph
- skipping the `IsAccessible(...)` live checks in `UpdateMovement(...)`

## Concrete patch target signatures

These are the signatures to patch or instrument for this research area:

- `Lopital.WalkComponent.SetDestination(Vector2i, int, MovementType)`
- `Lopital.WalkComponent.SetDestination(Vector2f, int, MovementType)`
- `Lopital.WalkComponent.UpdateDestinationSet()`
- `Lopital.WalkComponent.SetupJob(bool, bool, bool)`
- `Lopital.WalkComponent.UpdateLookingForPath()`
- `Lopital.WalkComponent.UpdateMovement(Floor, float)`
- `Lopital.WalkComponent.MoveFloor(bool)`
- `Lopital.WalkComponent.CheckElevator()`
- `GLib.PathfinderJob.ThreadFunction()`
- `GLib.Pathfinder.FindRoute(Vector2i, Vector2i, NavigationInfoProvider, int, int, int, int, PathfinderFlags)`
- `Lopital.Floor.IsAccessible(Vector2i, Vector2i, Vector2i, Vector2i, int, bool, bool)`
- `Lopital.Floor.GetMovementCost(Vector2i, int, bool)`
- `Lopital.Floor.GetMovementCost(Vector2i, Direction, bool)`
- `Lopital.Floor.IsThroughDoor(Vector2i, Direction)`
- `GLib.GridMap.Recalculate(int, TileWalls[,], Tile[,], AccessRights[,], AccessRights[,], List<TileObject>)`
- `GLib.GridMap.GetWaypoints(int, Vector2i, int, Vector2i, AccessRights)`
- `GLib.GridMap.GetDistance(int, Vector2i, int, Vector2i, AccessRights)`

## Bottom line

The expensive part is not just route solving. It is the repeated request pattern plus the amount of live state folded into validity:

- access rights
- room restrictions
- dynamic blocking objects
- elevator and stair graphs
- attached stretcher/patient state

The best next optimization is still request dedupe and profiling, not broad route memoization.
