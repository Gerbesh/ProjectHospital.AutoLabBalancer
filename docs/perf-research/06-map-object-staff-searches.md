# Performance Research 06: Map/Object/Staff Searches

Scope: `MapScriptInterface` hot methods involved in object, center-object, staff, and dirty-tile selection. Sources reviewed: `docs/performance-investigation.md`, `src/PerformanceOptimizations.cs`, `src/SchedulingEngine.cs`, `src/ProductivityTweaks.cs`, and decompiled `Assembly-CSharp.dll` via `ilspycmd`.

## What Is Hot

The relevant vanilla methods are mostly selection helpers, but they sit on hot AI loops and often run immediately before a mutation such as reserve, clean, move, or destination change.

Observed hot methods in the decompiled `MapScriptInterface`:

- Doctor search:
  - `FindClosestDoctorWithQualification(...)`
  - `FindClosestFreeDoctorWithQualification(...)`
  - `FindClosestNurseWithQualification(...)`
  - `FindClosestFreeNurseWithQualification(...)`
  - `FindClosestFreeMedicalEmployee(...)`
  - nurse/lab/janitor assigned searches such as:
    - `FindNurseAssignedToARoomType(...)`
    - `FindLabSpecialistAssingedToARoomTag(...)`
    - `FindLabSpecialistAssingedToARoomTagLowestWorkload(...)`
    - `FindLabSpecialistAssingedToARoomType(...)`
    - `FindLabSpecialistAssingedToRoom(...)`
    - `FindJanitorAssignedToARoomType(...)`
    - `FindJanitorAssignedToARoomTagLowestWorkload(...)`
    - `FindJanitorAssignedAssignedToRoom(...)`
- Object search:
  - `FindClosestFreeObjectWithTags(...)`
  - `FindClosestFreeObjectWithTagsAndRoomTags(...)`
  - `FindClosestFreeObjectWithTag(...)`
  - `FindClosestObjectWithTag(...)`
  - `FindClosestObjectWithTags(...)`
  - `FindClosestCenterObjectWithTag(...)`
  - `FindClosestCenterObjectWithTagShortestPath(...)`
  - `FindClosestWorkspace(...)`
- Dirty-tile search:
  - `FindDirtiestTileInRoomWithMatchingAssignmentAnyFloor(...)`
  - `FindDirtiestTileInAnyUnreservedRoomAnyFloor(...)`
  - `FindDirtiestTileInARoom(...)`
  - `FindClosestDirtyTileInARoom(...)`
  - `FindClosestDirtyIndoorsTile(...)`

These are consistent with the existing investigation note: staff idle logic, janitor logic, and overlay/analytics code repeatedly ask the same map questions and then act on the result.

## Current Caches

The current mod-side optimization layer is already using short-lived result caches, not durable indexes.

- `ObjectSearchCache`
  - Targets `FindClosestFreeObjectWithTags(...)` and `FindClosestFreeObjectWithTag(...)` variants.
  - TTL: `ObjectSearchCacheTtlSeconds`, default `0.35s`.
  - Validation on read: `IsValidFreeObject(...)` checks `IsValid()`, `User == null`, `Owner == null`, and suppresses stale/broken results.
- `CenterObjectSearchCache`
  - Targets center/object searches including `FindClosestCenterObjectWithTag(...)`, `FindClosestCenterObjectWithTagShortestPath(...)`, `FindClosestObjectWithTag(...)`, and `FindClosestObjectWithTags(...)`.
  - TTL: half of object TTL, clamped to at least `0.05s`.
  - Validation on read: `IsValidTileObject(...)`, and stale invalid entries are removed.
- `EntitySearchCache`
  - Targets staff selection methods returning `Entity`, including doctor, nurse, lab specialist, and janitor assignment searches.
  - TTL: `DoctorSearchCacheTtlSeconds`, default `0.35s`.
  - Validation on read: `IsValidStaffEntity(...)` plus a `mustBeFree` check inferred from the call shape.

The caches are intentionally exact-match caches. The key includes the method name and the full argument list, with collection and vector values serialized through `BuildArgKey(...)`.

## Read-Only vs Mutation Boundary

These methods are best treated as `pure-ish` selectors, not as truly pure functions.

Read-only enough to cache:

- Staff selectors that only choose a candidate from a department roster.
- Object selectors that only scan current room or department objects.
- Dirty-tile selectors that only inspect tile state.

Not read-only:

- `CleanTile(...)`
- `ReserveTile(...)`
- `MoveObject(...)`

These mutate the map state directly. Any cache that feeds them must assume the selected result can become invalid immediately after the selection.

## Validity Checks That Must Remain

The vanilla methods are only safe to reuse when the cached result is revalidated against the current world state.

### Staff

The decompiled staff searches depend on all of the following being still true:

- entity still exists and `IsValid()`
- not fired
- shift still matches when `hasToBeFree` or assignment rules require it
- staff is still free when the query asked for free staff
- skill/qualification still matches
- role still matches
- home room still exists and still matches the room type or room tag condition
- the staff member is still in the same department
- the staff member was not already selected in the active `ProcedureScene`

For `FindClosestFreeMedicalEmployee(...)`, the search is even broader:

- exclude janitors and paramedics
- exclude already selected doctors/nurses
- exclude staff whose behavior-specific `IsFree(patient)` has turned false

### Objects

The object searches are only safe when the selected `TileObject` still satisfies:

- `IsValid()`
- not broken
- not occupied by another `User` or `Owner` when a free object was requested
- still in a room that matches the room type/tag filter
- still on a tile that satisfies `AccessRights`
- still allowed by `allowedOutsideOfRoom`, `roomTags`, and `preferredDepartment` checks
- for some searches, still free of attachments or still compatible with composite parent state

The decompiled `FindClosestFreeObjectWithTags(...)` also rejects objects if:

- the room is invalid for the current access mode
- the character is hospitalized and the room accepts outpatients
- the tile is blocked by attachment rules
- the composite parent has unusable parts

### Dirt

Dirty-tile searches are only safe while the tile remains:

- dirty at or above the threshold
- unreserved
- not occupied by a user
- not blocked by tile objects
- still in a traversable accessibility state

The selector itself is read-only, but it is highly sensitive to later mutations such as cleaning, movement, reservation, or object placement.

## Invalidation Events

There is no event-driven invalidation layer yet. The current implementation relies on TTL plus late validation.

That is acceptable for a short retry window, but not as a long-lived state model.

Invalidate or bypass cached search results when any of the following changes:

- staff:
  - hire/fire
  - department transfer
  - shift change
  - training state change
  - role/skill change
  - home-room reassignment
  - current patient/procedure assignment
  - reservation/free state change
- objects:
  - user/owner assignment
  - broken/restored state
  - move, pickup, attach, detach, or composite-part state change
  - room validity change
  - department reassignment
  - access-rights change on the tile or room
- dirt and tiles:
  - `CleanTile(...)`
  - dirt accumulation / `SetDirty(...)`
  - `MoveObject(...)`
  - `ReserveTile(...)`
  - room reservation changes
  - tile accessibility / foundation changes

For the current mod scope, event invalidation is most useful for the broadest scans:

- department staff search caches
- room/object search caches
- dirty-tile summaries

Those are the places where TTL alone can still return a technically valid but already suboptimal answer.

## Where TTL Is Enough

TTL caching is sufficient when the same query is likely to repeat within a few frames and the result is immediately revalidated before use.

Good TTL-only candidates:

- `FindClosestFreeObjectWithTag(s)(...)`
- `FindClosestCenterObjectWithTagShortestPath(...)`
- doctor/nurse/lab/janitor exact-match searches that are repeatedly polled by the same idle branch
- nurse task-board gating snapshots
- reservation failure negative cache

Why this works:

- the call sites are repetitive
- the argument set is stable over a short window
- the result is consumed almost immediately
- validation can cheaply reject stale entries

## Where an Index Is Better

TTL alone stops duplicate work, but it does not reduce the cost of the first lookup. For methods that still scan a whole department or a whole floor, an index is the better shape.

### Index by department + role + shift + skill/tag

This is the right long-term structure for staff searches:

- `FindClosestDoctorWithQualification(...)`
- `FindClosestFreeDoctorWithQualification(...)`
- `FindClosestNurseWithQualification(...)`
- `FindClosestFreeNurseWithQualification(...)`
- `FindNurseAssignedToARoomType(...)`
- `FindLabSpecialistAssingedToARoomTag(...)`
- `FindLabSpecialistAssingedToARoomTagLowestWorkload(...)`
- `FindLabSpecialistAssingedToARoomType(...)`
- `FindLabSpecialistAssingedToRoom(...)`
- `FindJanitorAssignedToARoomType(...)`
- `FindJanitorAssignedToARoomTagLowestWorkload(...)`
- `FindJanitorAssignedAssignedToRoom(...)`

Why an index helps:

- these methods all iterate department lists linearly
- most of the filters are categorical, not geometric
- several of them only differ by tag, room type, qualification, or free-state
- workload variants then do a second pass over the already narrowed candidate list

Best index shape:

- department
- staff role
- shift
- qualification / room type / room tag
- free vs busy
- optional workload bucket

This should be backed by invalidation events, not just TTL, because staffing, training, assignment, and firing are discrete state changes.

### Index by department + floor + room tag + object tag

This is the right long-term structure for object searches:

- `FindClosestFreeObjectWithTags(...)`
- `FindClosestFreeObjectWithTagsAndRoomTags(...)`
- `FindClosestFreeObjectWithTag(...)`
- `FindClosestObjectWithTag(...)`
- `FindClosestObjectWithTags(...)`
- `FindClosestCenterObjectWithTag(...)`
- `FindClosestCenterObjectWithTagShortestPath(...)`
- `FindClosestWorkspace(...)`

Why an index helps:

- current implementations scan all department objects or all tiles in a room/floor
- the query dimensions are mostly categorical: tag, room tag, access rights, floor, free state, department
- the path-based variant still needs a candidate set before path scoring

Best index shape:

- department
- floor
- room type / room tag
- object tag
- access-rights bucket
- free-state

This is especially valuable for repeated AI calls that ask for slightly different tags or room tags; a TTL cache only helps if the exact argument tuple repeats.

### Dirty-tile summaries instead of repeated scans

Dirty-tile methods are the strongest candidate for a structural index:

- `FindDirtiestTileInRoomWithMatchingAssignmentAnyFloor(...)`
- `FindDirtiestTileInAnyUnreservedRoomAnyFloor(...)`
- `FindDirtiestTileInARoom(...)`
- `FindClosestDirtyTileInARoom(...)`
- `FindClosestDirtyIndoorsTile(...)`

Why index instead of TTL:

- the methods walk tile grids directly
- they are sensitive to every dirt, reservation, and occupancy change
- the same room can be re-scanned many times across consecutive frames
- cleanliness is a mutable property with frequent invalidation points

Best index shape:

- floor
- room
- dirt bucket or max dirt level
- blood-only vs general dirt
- reserved/unreserved
- accessibility

For janitor logic, this is a better fit than caching a single chosen tile because the chosen tile changes whenever any of the above fields change.

## Notes On The Existing Scheduling Index

`SchedulingEngineService` already behaves like a central read-only runtime index.

It rebuilds a department snapshot every `SchedulingEngineIntervalSeconds` and keeps:

- per-department task scores
- free staff counts
- dispatch recommendations
- patient and staff gating answers

This is not a replacement for `MapScriptInterface`, but it is a useful model:

- gather once on the main thread
- validate freshness with a max-age guard
- reuse the indexed snapshot for repeated decisions

That pattern is a better fit for department-level staff and dirty-task decisions than more exact-match caches.

## Bottom Line

- Short-lived caches are already correct for repeated identical queries, provided every reuse is revalidated.
- Exact-match TTL caches are good for the current hot call sites, especially where the same AI branch repeats the same lookup.
- Staff searches and dirty-tile searches are the clearest candidates for a structural index by department, role, floor, tag, and room type.
- Object searches sit in the middle: TTL helps now, but a tag/floor/department index is the real win if the scans stay hot.

