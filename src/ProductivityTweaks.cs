using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProjectHospital.AutoLabBalancer
{
    internal static class ProductivityTweaksService
    {
        private static readonly Dictionary<object, float> HighPriorityCleanupRooms = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<object, ReservationWatch> EmployeeReservations = new Dictionary<object, ReservationWatch>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<object, ReservationWatch> RoomReservations = new Dictionary<object, ReservationWatch>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<object, NurseCleanupJob> NurseCleanupJobs = new Dictionary<object, NurseCleanupJob>(ReferenceEqualityComparer.Instance);
        private static float _nextWatchdogAt;
        private static bool _findingFlexibleTransportObject;

        public static int HighPriorityCleanupRoomCount
        {
            get { return HighPriorityCleanupRooms.Count; }
        }

        public static int NurseCleanupJobCount
        {
            get { return NurseCleanupJobs.Count; }
        }

        public static void Tick(float now)
        {
            if (!IsEnabled())
            {
                return;
            }

            PruneCleanupRooms(now);

            if (!RuntimeSettings.Config.EnableStuckReservationCleanup.Value || now < _nextWatchdogAt)
            {
                return;
            }

            _nextWatchdogAt = now + 10f;
            RunReservationWatchdog(now);
        }

        public static void MarkPostSurgeryRoom(object procedureScript)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnablePostSurgeryCleanupPriority.Value)
            {
                return;
            }

            try
            {
                var state = ReflectionHelpers.GetField(procedureScript, "m_stateData");
                var scene = ReflectionHelpers.GetField(state, "m_procedureScene");
                var room = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(scene, "m_room"));
                if (room == null)
                {
                    return;
                }

                var expires = UnityEngine.Time.realtimeSinceStartup + Math.Max(1f, RuntimeSettings.Config.ORCleanupPriorityDurationSeconds.Value);
                HighPriorityCleanupRooms[room] = expires;
                RuntimeCounters.ORCleanupPrioritiesCreated++;
                Debug("Added post-surgery cleanup priority for room " + Describe(room) + " until " + expires.ToString("0.0") + ".");
            }
            catch (Exception ex)
            {
                LogError("Post-surgery cleanup priority failed: " + ex);
            }
        }

        public static bool TrySelectPriorityCleanup(object janitor)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnablePostSurgeryCleanupPriority.Value || janitor == null)
            {
                return false;
            }

            try
            {
                if (!IsFreeBehavior(janitor) || IsEmployeeBusy(GetEntityFromComponent(janitor)))
                {
                    return false;
                }

                var employee = ReflectionHelpers.GetComponentByTypeName(GetEntityFromComponent(janitor), "Lopital.EmployeeComponent");
                var department = GetEmployeeDepartment(employee);
                var room = FindBestCleanupRoom(janitor, department, UnityEngine.Time.realtimeSinceStartup);
                if (room == null)
                {
                    return false;
                }

                return TryForceJanitorRoomSelection(janitor, room);
            }
            catch (Exception ex)
            {
                LogError("Priority cleanup selection failed: " + ex);
                return false;
            }
        }

        public static bool ShouldSuppressFreeTime(object procedure, object department)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnableFreeTimeSuppression.Value)
            {
                return false;
            }

            try
            {
                if (!IsFreeTimeProcedure(procedure))
                {
                    return false;
                }

                if (!RuntimeSettings.Config.SuppressFreeTimeWhenDepartmentBusy.Value)
                {
                    Debug("Suppressed free-time because department-busy gating is disabled.");
                    RuntimeCounters.FreeTimeSuppressed++;
                    return true;
                }

                if (IsDepartmentBusy(department))
                {
                    Debug("Suppressed free-time for busy department " + Describe(department) + ".");
                    RuntimeCounters.FreeTimeSuppressed++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("Free-time suppression check failed: " + ex);
            }

            return false;
        }

        public static bool TryHandleNurseAssistedCleanup(object nurse, float deltaTime)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnableNurseAssistedORCleanup.Value || nurse == null || deltaTime <= 0f)
            {
                return false;
            }

            try
            {
                NurseCleanupJob job;
                if (NurseCleanupJobs.TryGetValue(nurse, out job))
                {
                    UpdateNurseCleanupJob(nurse, job);
                    return true;
                }

                if (!CanStartNurseCleanup(nurse))
                {
                    return false;
                }

                var employee = ReflectionHelpers.GetComponentByTypeName(GetEntityFromComponent(nurse), "Lopital.EmployeeComponent");
                var department = GetEmployeeDepartment(employee);
                if (IsDepartmentBusyWithNurseWork(department))
                {
                    return false;
                }

                var room = FindBestCleanupRoom(nurse, department, UnityEngine.Time.realtimeSinceStartup);
                if (room == null)
                {
                    return false;
                }

                var floor = InvokeInt(room, "GetFloorIndex");
                if (!floor.HasValue)
                {
                    return false;
                }

                var tile = FindDirtyTile(room, floor.Value);
                if (!IsValidVector2(tile))
                {
                    return false;
                }

                ReserveRoom(room, GetEntityFromComponent(nurse));
                var expires = UnityEngine.Time.realtimeSinceStartup + Math.Max(1f, RuntimeSettings.Config.NurseORCleanupMaxDurationSeconds.Value);
                job = new NurseCleanupJob(room, tile, floor.Value, expires);
                NurseCleanupJobs[nurse] = job;
                SetWalkDestination(nurse, tile, floor.Value);
                RuntimeCounters.NurseCleanupJobsStarted++;
                Debug("Nurse-assisted OR cleanup started for " + Describe(room) + ".");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Nurse-assisted OR cleanup failed: " + ex);
                EndNurseCleanup(nurse, "exception");
                return false;
            }
        }

        public static object TryFindFlexibleTransportObject(object original, object position, int floorIndex, object department, string tag, object accessRights, bool needsToBeFree, bool onlyComposite)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnableFlexibleStretcherPickup.Value || _findingFlexibleTransportObject)
            {
                return original;
            }

            if (!needsToBeFree || (tag != "stretcher_patient" && tag != "wheelchair_patient"))
            {
                return original;
            }

            if (original != null)
            {
                return original;
            }

            try
            {
                _findingFlexibleTransportObject = true;
                var fallback = FindClosestTransportObjectAcrossDepartments(position, floorIndex, tag, needsToBeFree, onlyComposite);
                if (fallback != null)
                {
                    RuntimeCounters.FlexibleTransportFallbacks++;
                    Debug("Flexible stretcher pickup selected fallback " + Describe(fallback) + " for tag " + tag + ".");
                    return fallback;
                }
            }
            catch (Exception ex)
            {
                LogError("Flexible stretcher pickup failed: " + ex);
            }
            finally
            {
                _findingFlexibleTransportObject = false;
            }

            return original;
        }

        public static bool IsEmergencyContext(object behavior)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnableEmergencyRunSpeedBoost.Value || behavior == null)
            {
                return false;
            }

            try
            {
                var typeName = behavior.GetType().FullName;
                if (typeName != "Lopital.BehaviorNurse" && typeName != "Lopital.BehaviorDoctor")
                {
                    return false;
                }

                var patient = GetCurrentOrReservedPatient(behavior);
                if (IsCriticalOrCollapsedPatient(patient))
                {
                    Debug("Emergency speed boost context detected for " + typeName + ".");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("Emergency context check failed: " + ex);
            }

            return false;
        }

        public static bool ShouldBoostRunningMovement(object walkComponent)
        {
            if (!IsEnabled() || !RuntimeSettings.Config.EnableEmergencyRunSpeedBoost.Value || walkComponent == null)
            {
                return false;
            }

            try
            {
                var state = ReflectionHelpers.GetField(walkComponent, "m_state");
                var movementType = ReflectionHelpers.GetField(state, "m_movementType");
                if (movementType == null || !string.Equals(movementType.ToString(), "RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var entity = GetEntityFromComponent(walkComponent);
                var doctor = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorDoctor");
                if (doctor != null)
                {
                    return IsEmergencyContext(doctor);
                }

                var nurse = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorNurse");
                return nurse != null && IsEmergencyContext(nurse);
            }
            catch (Exception ex)
            {
                LogError("Emergency movement boost check failed: " + ex);
                return false;
            }
        }

        public static bool TryHandleAfterExaminationCheck(object hospitalization, float deltaTime)
        {
            if (!IsEnabled()
                || RuntimeSettings.Config == null
                || !RuntimeSettings.Config.EnableChainedHospitalizedExaminations.Value
                || hospitalization == null)
            {
                return false;
            }

            try
            {
                var state = ReflectionHelpers.GetField(hospitalization, "m_state");
                if (!Equals(ReflectionHelpers.GetField(state, "m_oustideRoom"), true))
                {
                    return false;
                }

                var entity = GetEntityFromComponent(hospitalization);
                var patient = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.BehaviorPatient");
                if (IsPatientGone(patient))
                {
                    return false;
                }

                var selectNextStep = AccessTools.Method(hospitalization.GetType(), "SelectNextStep");
                var selected = selectNextStep != null && Equals(selectNextStep.Invoke(hospitalization, new object[] { deltaTime }), true);
                if (selected)
                {
                    return true;
                }

                if (HasPlannedExaminations(entity) && IsWaitingForExaminationTransport(state))
                {
                    if (ShouldRetryTransportReservation(state))
                    {
                        RetryTransportReservation(hospitalization, entity, state, "chained examination transport wait");
                    }

                    Debug("Keeping hospitalized patient outside room for chained planned examination.");
                    return true;
                }

                var sendBack = AccessTools.Method(hospitalization.GetType(), "SendBackToRoom");
                if (sendBack != null)
                {
                    sendBack.Invoke(hospitalization, null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("Chained hospitalized examination handling failed: " + ex);
            }

            return false;
        }

        private static bool TryForceJanitorRoomSelection(object janitor, object room)
        {
            var state = ReflectionHelpers.GetField(janitor, "m_state");
            var assignedRooms = ReflectionHelpers.GetField(state, "m_assignedRooms") as IList;
            var original = assignedRooms == null ? null : assignedRooms.Cast<object>().ToList();

            try
            {
                InvokeVoid(janitor, "AddAssignedRoom", room);
                var selected = InvokeBool(janitor, "TryToSelectTileInARoom");
                if (selected)
                {
                    Debug("Janitor selected high-priority OR cleanup room " + Describe(room) + ".");
                    return true;
                }

                return false;
            }
            finally
            {
                if (assignedRooms != null && original != null)
                {
                    assignedRooms.Clear();
                    foreach (var item in original)
                    {
                        assignedRooms.Add(item);
                    }
                }
            }
        }

        private static object FindBestCleanupRoom(object janitor, object department, float now)
        {
            PruneCleanupRooms(now);

            foreach (var pair in HighPriorityCleanupRooms.ToList())
            {
                var room = pair.Key;
                if (room == null || IsRoomReserved(room) || !IsRoomDirty(room))
                {
                    HighPriorityCleanupRooms.Remove(room);
                    continue;
                }

                if (department == null || ReferenceEquals(GetRoomDepartment(room), department))
                {
                    return room;
                }
            }

            return null;
        }

        private static void UpdateNurseCleanupJob(object nurse, NurseCleanupJob job)
        {
            if (UnityEngine.Time.realtimeSinceStartup > job.ExpiresAt || !CanContinueNurseCleanup(nurse, job))
            {
                EndNurseCleanup(nurse, "expired or unsafe");
                return;
            }

            var walk = ReflectionHelpers.GetComponentByTypeName(GetEntityFromComponent(nurse), "Lopital.WalkComponent");
            if (InvokeBool(walk, "IsBusy"))
            {
                return;
            }

            CleanTile(job.Tile, job.FloorIndex);
            RuntimeCounters.NurseORTilesCleaned++;
            Debug("Nurse cleaned OR tile in " + Describe(job.Room) + ".");

            var nextTile = FindDirtyTile(job.Room, job.FloorIndex);
            if (IsValidVector2(nextTile))
            {
                job.Tile = nextTile;
                SetWalkDestination(nurse, nextTile, job.FloorIndex);
                return;
            }

            EndNurseCleanup(nurse, "room clean");
        }

        private static bool CanStartNurseCleanup(object nurse)
        {
            if (!IsFreeBehavior(nurse))
            {
                return false;
            }

            var entity = GetEntityFromComponent(nurse);
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            if (employee == null || ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
            {
                return false;
            }

            var currentPatient = GetProperty(nurse, "CurrentPatient");
            if (currentPatient != null)
            {
                return false;
            }

            return HasEmployeeRole(employee, "EMPL_ROLE_SURGERY_NURSE");
        }

        private static bool CanContinueNurseCleanup(object nurse, NurseCleanupJob job)
        {
            if (nurse == null || job == null || job.Room == null)
            {
                return false;
            }

            if (GetProperty(nurse, "CurrentPatient") != null)
            {
                return false;
            }

            var entity = GetEntityFromComponent(nurse);
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            if (employee != null && ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
            {
                return false;
            }

            if (IsDepartmentBusyWithNurseWork(GetEmployeeDepartment(employee)))
            {
                return false;
            }

            var reservedBy = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(ReflectionHelpers.GetField(job.Room, "m_roomPersistentData"), "m_reservedByCharacter"));
            return reservedBy == null || ReferenceEquals(reservedBy, entity);
        }

        private static bool IsDepartmentBusyWithNurseWork(object department)
        {
            if (department == null)
            {
                return false;
            }

            return InvokeBool(department, "HasAnyCriticalPatients")
                || InvokeBool(department, "HasWaitingSurgery")
                || InvokeBool(department, "HasAnyCriticalSurgeryScheduled")
                || InvokeBool(department, "HasAnyHospitalizedPatientsWithScheduledProcedures");
        }

        private static void EndNurseCleanup(object nurse, string reason)
        {
            NurseCleanupJob job;
            if (!NurseCleanupJobs.TryGetValue(nurse, out job))
            {
                return;
            }

            try
            {
                var entity = GetEntityFromComponent(nurse);
                var roomState = ReflectionHelpers.GetField(job.Room, "m_roomPersistentData");
                var reservedBy = ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(roomState, "m_reservedByCharacter"));
                if (reservedBy == null || ReferenceEquals(reservedBy, entity))
                {
                    SetField(roomState, "m_reservedByCharacter", DefaultValueForField(roomState, "m_reservedByCharacter"));
                }
            }
            finally
            {
                NurseCleanupJobs.Remove(nurse);
                Debug("Nurse-assisted OR cleanup ended: " + reason + ".");
            }
        }

        private static object FindClosestTransportObjectAcrossDepartments(object position, int floorIndex, string tag, bool needsToBeFree, bool onlyComposite)
        {
            var hospital = GetHospital();
            object best = null;
            var bestDistance = int.MaxValue;

            foreach (var dept in ReflectionHelpers.GetEnumerableField(hospital, "m_departments"))
            {
                var state = ReflectionHelpers.GetField(dept, "m_departmentPersistentData");
                foreach (var pointer in ReflectionHelpers.GetEnumerableField(state, "m_objects"))
                {
                    var candidate = ReflectionHelpers.ResolvePointer(pointer);
                    if (!IsTransportObjectCandidate(candidate, tag, needsToBeFree, onlyComposite))
                    {
                        continue;
                    }

                    var candidateState = ReflectionHelpers.GetField(candidate, "m_state");
                    var candidatePosition = ReflectionHelpers.GetField(candidateState, "m_position");
                    var candidateFloor = InvokeInt(candidate, "GetFloorIndex");
                    if (!candidateFloor.HasValue)
                    {
                        continue;
                    }

                    var distance = SquaredDistance(position, candidatePosition) + Math.Abs(floorIndex - candidateFloor.Value) * 10000;
                    if (distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
            }

            return best;
        }

        private static bool IsTransportObjectCandidate(object candidate, string tag, bool needsToBeFree, bool onlyComposite)
        {
            if (candidate == null || !InvokeBool(candidate, "IsValid") || InvokeBool(candidate, "IsBroken"))
            {
                return false;
            }

            if (!InvokeBool(candidate, "HasTag", tag))
            {
                return false;
            }

            if (onlyComposite && ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(ReflectionHelpers.GetField(candidate, "m_state"), "m_compositeParent")) == null)
            {
                return false;
            }

            if (needsToBeFree && (GetProperty(candidate, "User") != null || GetProperty(candidate, "Owner") != null))
            {
                return false;
            }

            var state = ReflectionHelpers.GetField(candidate, "m_state");
            var error = ReflectionHelpers.GetField(state, "m_error");
            return error == null || Convert.ToInt32(error) <= 0;
        }

        private static void RunReservationWatchdog(float now)
        {
            try
            {
                var hospital = GetHospital();
                if (hospital == null)
                {
                    return;
                }

                WatchEmployeeReservations(hospital, now);
                WatchRoomReservations(hospital, now);
            }
            catch (Exception ex)
            {
                LogError("Reservation watchdog failed: " + ex);
            }
        }

        private static void WatchEmployeeReservations(object hospital, float now)
        {
            foreach (var character in ReflectionHelpers.GetEnumerableField(hospital, "m_characters"))
            {
                var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                if (employee == null)
                {
                    continue;
                }

                var state = ReflectionHelpers.GetField(employee, "m_state");
                var patientPointer = ReflectionHelpers.GetField(state, "m_reservedByPatient");
                var procedureLoc = ReflectionHelpers.GetField(state, "m_reservedForProcedureLocID") as string;
                var signature = PointerSignature(patientPointer) + "|" + (procedureLoc ?? string.Empty);

                if (string.IsNullOrEmpty(signature) || signature == "0|")
                {
                    EmployeeReservations.Remove(employee);
                    continue;
                }

                var watch = GetOrUpdateWatch(EmployeeReservations, employee, signature, now);
                if (now - watch.StartedAt < RuntimeSettings.Config.StuckReservationTimeoutSeconds.Value)
                {
                    continue;
                }

                var reservedPatient = ReflectionHelpers.ResolvePointer(patientPointer);
                if (reservedPatient == null && !ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
                {
                    SetField(state, "m_reservedByPatient", DefaultValueForField(state, "m_reservedByPatient"));
                    SetField(state, "m_reservedForProcedureLocID", null);
                    EmployeeReservations.Remove(employee);
                    RuntimeCounters.StuckReservationsCleared++;
                    LogInfo("Cleaned stuck employee reservation for " + Describe(character) + ": reserved patient no longer resolves.");
                }
            }
        }

        private static void WatchRoomReservations(object hospital, float now)
        {
            foreach (var department in ReflectionHelpers.GetEnumerableField(hospital, "m_departments"))
            {
                foreach (var room in EnumerateDepartmentRooms(department))
                {
                    var roomState = ReflectionHelpers.GetField(room, "m_roomPersistentData");
                    var reservedPointer = ReflectionHelpers.GetField(roomState, "m_reservedByCharacter");
                    var signature = PointerSignature(reservedPointer);
                    if (string.IsNullOrEmpty(signature) || signature == "0")
                    {
                        RoomReservations.Remove(room);
                        continue;
                    }

                    var watch = GetOrUpdateWatch(RoomReservations, room, signature, now);
                    if (now - watch.StartedAt < RuntimeSettings.Config.StuckReservationTimeoutSeconds.Value)
                    {
                        continue;
                    }

                    var character = ReflectionHelpers.ResolvePointer(reservedPointer);
                    var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
                    if (character == null || !IsEmployeeBusy(character) && !IsCurrentProcedureRoom(room))
                    {
                        SetField(roomState, "m_reservedByCharacter", DefaultValueForField(roomState, "m_reservedByCharacter"));
                        RoomReservations.Remove(room);
                        RuntimeCounters.StuckReservationsCleared++;
                        LogInfo("Cleaned stuck room reservation for " + Describe(room) + ": reserver is gone or idle.");
                    }
                }
            }
        }

        private static ReservationWatch GetOrUpdateWatch(Dictionary<object, ReservationWatch> watches, object key, string signature, float now)
        {
            ReservationWatch watch;
            if (!watches.TryGetValue(key, out watch) || watch.Signature != signature)
            {
                watch = new ReservationWatch(signature, now);
                watches[key] = watch;
            }

            return watch;
        }

        private static bool IsCurrentProcedureRoom(object room)
        {
            var state = ReflectionHelpers.GetField(room, "m_roomPersistentData");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_currentProcedureOwner")) != null;
        }

        private static bool IsRoomDirty(object room)
        {
            var map = GetMapScriptInterface();
            if (map == null || room == null)
            {
                return false;
            }

            var floor = InvokeInt(room, "GetFloorIndex");
            if (!floor.HasValue)
            {
                return false;
            }

            var method = AccessTools.Method(map.GetType(), "FindDirtiestTileInARoom");
            if (method == null)
            {
                return false;
            }

            var tile = method.Invoke(map, new[] { room, false, (object)floor.Value });
            return IsValidVector2(tile);
        }

        private static object FindDirtyTile(object room, int floorIndex)
        {
            var map = GetMapScriptInterface();
            var method = map == null ? null : AccessTools.Method(map.GetType(), "FindClosestDirtyTileInARoom");
            if (method != null)
            {
                var center = ReflectionHelpers.GetField(room, "m_center");
                var toVector2i = center == null ? null : AccessTools.Method(center.GetType(), "ToVector2i");
                var from = toVector2i == null ? null : toVector2i.Invoke(center, null);
                if (from != null)
                {
                    return method.Invoke(map, new[] { room, from });
                }
            }

            method = map == null ? null : AccessTools.Method(map.GetType(), "FindDirtiestTileInARoom");
            return method == null ? null : method.Invoke(map, new[] { room, false, (object)floorIndex });
        }

        private static void CleanTile(object tile, int floorIndex)
        {
            var map = GetMapScriptInterface();
            var method = map == null ? null : AccessTools.Method(map.GetType(), "CleanTile");
            if (method != null)
            {
                method.Invoke(map, new[] { tile, (object)floorIndex });
            }
        }

        private static bool IsRoomReserved(object room)
        {
            var state = ReflectionHelpers.GetField(room, "m_roomPersistentData");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_reservedByCharacter")) != null;
        }

        private static bool IsDepartmentBusy(object department)
        {
            if (department == null)
            {
                return HighPriorityCleanupRooms.Count > 0;
            }

            return InvokeBool(department, "HasAnyCriticalPatients")
                || InvokeBool(department, "HasWaitingSurgery")
                || InvokeBool(department, "HasAnyCriticalSurgeryScheduled")
                || InvokeBool(department, "HasAnyHospitalizedPatientsWithScheduledProcedures")
                || InvokeBool(department, "HasAnyWaitingPatients")
                || HighPriorityCleanupRooms.Count > 0;
        }

        private static bool IsFreeTimeProcedure(object procedure)
        {
            if (procedure == null)
            {
                return false;
            }

            var script = ReflectionHelpers.GetStringProperty(procedure, "ProcedureScript");
            if (string.IsNullOrEmpty(script))
            {
                script = procedure.ToString();
            }

            return script != null && script.IndexOf("FreeTime", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static object GetCurrentOrReservedPatient(object behavior)
        {
            var property = behavior.GetType().GetProperty("CurrentPatient", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var patient = property == null ? null : property.GetValue(behavior, null);
            if (patient != null)
            {
                return patient;
            }

            var entity = GetEntityFromComponent(behavior);
            var employee = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.EmployeeComponent");
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_reservedByPatient"));
        }

        private static bool IsCriticalOrCollapsedPatient(object patient)
        {
            if (patient == null)
            {
                return false;
            }

            var behavior = ReflectionHelpers.GetComponentByTypeName(patient, "Lopital.BehaviorPatient");
            if (behavior != null)
            {
                if (InvokeBool(behavior, "HasCriticalSurgeryPlanned"))
                {
                    return true;
                }

                var state = ReflectionHelpers.GetField(behavior, "m_state");
                var patientState = ReflectionHelpers.GetField(state, "m_patientState");
                if (patientState != null)
                {
                    var text = patientState.ToString();
                    if (text.IndexOf("Collaps", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Stabil", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            var hospitalization = ReflectionHelpers.GetComponentByTypeName(patient, "Lopital.HospitalizationComponent");
            if (hospitalization != null && InvokeBool(hospitalization, "WillCollapse"))
            {
                return true;
            }

            return false;
        }

        private static object GetEmployeeDepartment(object employee)
        {
            var state = ReflectionHelpers.GetField(employee, "m_state");
            return ReflectionHelpers.ResolvePointer(ReflectionHelpers.GetField(state, "m_department"));
        }

        private static object GetRoomDepartment(object room)
        {
            var hospital = GetHospital();
            if (hospital == null || room == null)
            {
                return null;
            }

            foreach (var department in ReflectionHelpers.GetEnumerableField(hospital, "m_departments"))
            {
                foreach (var candidate in EnumerateDepartmentRooms(department))
                {
                    if (ReferenceEquals(candidate, room))
                    {
                        return department;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<object> EnumerateDepartmentRooms(object department)
        {
            var state = ReflectionHelpers.GetField(department, "m_departmentPersistentData");
            foreach (var pointer in ReflectionHelpers.GetEnumerableField(state, "m_rooms"))
            {
                var room = ReflectionHelpers.ResolvePointer(pointer);
                if (room != null)
                {
                    yield return room;
                }
            }
        }

        private static object GetHospital()
        {
            var type = AccessTools.TypeByName("Lopital.Hospital");
            var property = type == null ? null : AccessTools.Property(type, "Instance");
            return property == null ? null : property.GetValue(null, null);
        }

        private static object GetMapScriptInterface()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            var property = type == null ? null : AccessTools.Property(type, "Instance");
            return property == null ? null : property.GetValue(null, null);
        }

        private static object GetEntityFromComponent(object component)
        {
            return ReflectionHelpers.GetField(component, "m_entity");
        }

        private static object GetProperty(object instance, string propertyName)
        {
            if (instance == null)
            {
                return null;
            }

            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static bool IsFreeBehavior(object behavior)
        {
            return InvokeBool(behavior, "IsFree") && !InvokeBool(behavior, "GetReserved");
        }

        private static bool IsEmployeeBusy(object character)
        {
            var employee = ReflectionHelpers.GetComponentByTypeName(character, "Lopital.EmployeeComponent");
            if (employee != null && ReflectionHelpers.InvokeBool(employee, "IsPerformingAProcedure"))
            {
                return true;
            }

            foreach (var typeName in new[] { "Lopital.BehaviorNurse", "Lopital.BehaviorDoctor", "Lopital.BehaviorLabSpecialist", "Lopital.BehaviorJanitor" })
            {
                var behavior = ReflectionHelpers.GetComponentByTypeName(character, typeName);
                if (behavior != null && !IsFreeBehavior(behavior))
                {
                    return true;
                }
            }

            return false;
        }

        private static void PruneCleanupRooms(float now)
        {
            foreach (var pair in HighPriorityCleanupRooms.ToList())
            {
                if (pair.Value <= now || !IsRoomDirty(pair.Key))
                {
                    HighPriorityCleanupRooms.Remove(pair.Key);
                    Debug("Removed cleanup priority for room " + Describe(pair.Key) + ".");
                }
            }
        }

        private static bool IsEnabled()
        {
            return RuntimeSettings.Config != null && RuntimeSettings.Config.Enabled.Value;
        }

        private static bool IsValidVector2(object vector)
        {
            if (vector == null)
            {
                return false;
            }

            var x = ReflectionHelpers.GetField(vector, "m_x");
            var y = ReflectionHelpers.GetField(vector, "m_y");
            return x != null && y != null && Convert.ToInt32(x) >= 0 && Convert.ToInt32(y) >= 0;
        }

        private static bool InvokeBool(object instance, string methodName)
        {
            if (instance == null)
            {
                return false;
            }

            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method != null && Equals(method.Invoke(instance, null), true);
        }

        private static bool InvokeBool(object instance, string methodName, params object[] args)
        {
            if (instance == null)
            {
                return false;
            }

            var method = AccessTools.Method(instance.GetType(), methodName);
            return method != null && Equals(method.Invoke(instance, args), true);
        }

        private static int? InvokeInt(object instance, string methodName)
        {
            if (instance == null)
            {
                return null;
            }

            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return null;
            }

            return Convert.ToInt32(method.Invoke(instance, null));
        }

        private static void InvokeVoid(object instance, string methodName, params object[] args)
        {
            var method = instance == null ? null : AccessTools.Method(instance.GetType(), methodName);
            if (method != null)
            {
                method.Invoke(instance, args);
            }
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static void ReserveRoom(object room, object character)
        {
            var roomState = ReflectionHelpers.GetField(room, "m_roomPersistentData");
            SetPointerField(roomState, "m_reservedByCharacter", character);
        }

        private static void SetPointerField(object instance, string fieldName, object entity)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field == null)
            {
                return;
            }

            var value = entity == null ? Activator.CreateInstance(field.FieldType) : Activator.CreateInstance(field.FieldType, entity);
            field.SetValue(instance, value);
        }

        private static void SetWalkDestination(object behavior, object position, int floorIndex)
        {
            var walk = ReflectionHelpers.GetComponentByTypeName(GetEntityFromComponent(behavior), "Lopital.WalkComponent");
            if (walk == null || position == null)
            {
                return;
            }

            var movementType = AccessTools.TypeByName("Lopital.MovementType");
            if (movementType == null)
            {
                return;
            }

            var walking = movementType == null ? null : Enum.Parse(movementType, "WALKING");
            var method = walk.GetType().GetMethod("SetDestination", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { position.GetType(), typeof(int), movementType }, null);
            if (method != null)
            {
                method.Invoke(walk, new[] { position, (object)floorIndex, walking });
            }
        }

        private static bool HasEmployeeRole(object employee, string roleId)
        {
            var role = GetDatabaseEntry("GameDBEmployeeRole", roleId);
            var method = employee == null || role == null ? null : employee.GetType().GetMethod("HasRole", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { role.GetType() }, null);
            return method != null && Equals(method.Invoke(employee, new[] { role }), true);
        }

        private static bool IsPatientGone(object behaviorPatient)
        {
            var state = ReflectionHelpers.GetField(behaviorPatient, "m_state");
            if (state == null)
            {
                return true;
            }

            if (Equals(ReflectionHelpers.GetField(state, "m_sentAway"), true)
                || Equals(ReflectionHelpers.GetField(state, "m_sentHome"), true)
                || Equals(ReflectionHelpers.GetField(state, "m_deathTriggered"), true))
            {
                return true;
            }

            var patientState = ReflectionHelpers.GetField(state, "m_patientState");
            return patientState != null && string.Equals(patientState.ToString(), "Left", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasPlannedExaminations(object entity)
        {
            var procedure = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.ProcedureComponent");
            var state = ReflectionHelpers.GetField(procedure, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            var planned = ReflectionHelpers.GetField(queue, "m_plannedExaminationStates") as IList;
            return planned != null && planned.Count > 0;
        }

        private static bool IsWaitingForExaminationTransport(object hospitalizationState)
        {
            var status = ReflectionHelpers.GetField(hospitalizationState, "m_procedureReservationStatus");
            return status != null && string.Equals(status.ToString(), "WAITING_FOR_TRANSPORT_EXM", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldRetryTransportReservation(object hospitalizationState)
        {
            if (RuntimeSettings.Config == null || !RuntimeSettings.Config.EnableTransportReservationTimeout.Value)
            {
                return false;
            }

            var time = ReflectionHelpers.GetField(hospitalizationState, "m_timeInState");
            return time is float && (float)time >= Math.Max(10f, RuntimeSettings.Config.TransportReservationTimeoutSeconds.Value);
        }

        private static void RetryTransportReservation(object hospitalization, object entity, object hospitalizationState, string reason)
        {
            var procedure = ReflectionHelpers.GetComponentByTypeName(entity, "Lopital.ProcedureComponent");
            InvokeVoid(procedure, "CancelReservation");
            SetField(hospitalizationState, "m_procedureReservationStatus", DefaultValueForField(hospitalizationState, "m_procedureReservationStatus"));
            SetField(hospitalizationState, "m_timeInState", 0f);
            ClearProcedureReservationStatuses(procedure);
            RuntimeCounters.TransportReservationsRetried++;
            Debug("Retried stale transport/procedure reservation: " + reason + ".");
        }

        private static void ClearProcedureReservationStatuses(object procedure)
        {
            var state = ReflectionHelpers.GetField(procedure, "m_state");
            var queue = ReflectionHelpers.GetField(state, "m_procedureQueue");
            foreach (var fieldName in new[] { "m_plannedExaminationStates", "m_plannedTreatmentStates" })
            {
                foreach (var plannedState in ReflectionHelpers.GetEnumerableField(queue, fieldName))
                {
                    SetField(plannedState, "m_reservationStatus", DefaultValueForField(plannedState, "m_reservationStatus"));
                }
            }
        }

        private static object GetDatabaseEntry(string entryTypeName, string id)
        {
            var databaseType = AccessTools.TypeByName("Database");
            var instanceProperty = databaseType == null ? null : AccessTools.Property(databaseType, "Instance");
            var instance = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
            var entryType = AccessTools.TypeByName(entryTypeName);
            if (instance == null || entryType == null)
            {
                return null;
            }

            foreach (var method in AccessTools.GetDeclaredMethods(databaseType))
            {
                var parameters = method.GetParameters();
                if (method.Name == "GetEntry"
                    && method.IsGenericMethodDefinition
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(string))
                {
                    return method.MakeGenericMethod(entryType).Invoke(instance, new object[] { id });
                }
            }

            return null;
        }

        private static object DefaultValueForField(object instance, string fieldName)
        {
            var field = instance == null ? null : AccessTools.Field(instance.GetType(), fieldName);
            if (field == null)
            {
                return null;
            }

            return field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType) : null;
        }

        private static string PointerSignature(object pointer)
        {
            if (pointer == null)
            {
                return string.Empty;
            }

            var entityId = ReflectionHelpers.GetField(pointer, "m_entityID");
            return entityId == null ? pointer.ToString() : Convert.ToString(entityId);
        }

        private static int SquaredDistance(object a, object b)
        {
            if (a == null || b == null)
            {
                return int.MaxValue / 2;
            }

            var ax = Convert.ToInt32(ReflectionHelpers.GetField(a, "m_x"));
            var ay = Convert.ToInt32(ReflectionHelpers.GetField(a, "m_y"));
            var bx = Convert.ToInt32(ReflectionHelpers.GetField(b, "m_x"));
            var by = Convert.ToInt32(ReflectionHelpers.GetField(b, "m_y"));
            var dx = ax - bx;
            var dy = ay - by;
            return dx * dx + dy * dy;
        }

        private static string Describe(object value)
        {
            return value == null ? "<null>" : value.ToString();
        }

        private static void Debug(string message)
        {
            if (RuntimeSettings.ProductivityDebug && RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogDebug("[ProductivityTweaks] " + message);
            }
        }

        private static void LogInfo(string message)
        {
            if (RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogInfo("[ProductivityTweaks] " + message);
            }
        }

        private static void LogError(string message)
        {
            if (RuntimeSettings.Logger != null)
            {
                RuntimeSettings.Logger.LogError("[ProductivityTweaks] " + message);
            }
        }

        private sealed class ReservationWatch
        {
            public ReservationWatch(string signature, float startedAt)
            {
                Signature = signature;
                StartedAt = startedAt;
            }

            public string Signature { get; private set; }
            public float StartedAt { get; private set; }
        }

        private sealed class NurseCleanupJob
        {
            public NurseCleanupJob(object room, object tile, int floorIndex, float expiresAt)
            {
                Room = room;
                Tile = tile;
                FloorIndex = floorIndex;
                ExpiresAt = expiresAt;
            }

            public object Room { get; private set; }
            public object Tile { get; set; }
            public int FloorIndex { get; private set; }
            public float ExpiresAt { get; private set; }
        }
    }

    [HarmonyPatch]
    internal static class PostSurgeryCleanupPriorityPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.ProcedureScriptTreatmentSurgery");
            return type == null ? null : AccessTools.Method(type, "UpdateStateProcedureFinished");
        }

        private static void Postfix(object __instance)
        {
            ProductivityTweaksService.MarkPostSurgeryRoom(__instance);
        }
    }

    [HarmonyPatch]
    internal static class JanitorPriorityCleanupPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorJanitor");
            return type == null ? null : AccessTools.Method(type, "SelectNextAction");
        }

        private static bool Prefix(object __instance)
        {
            return !ProductivityTweaksService.TrySelectPriorityCleanup(__instance);
        }
    }

    [HarmonyPatch]
    internal static class NurseAssistedORCleanupPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.BehaviorNurse");
            return type == null ? null : AccessTools.Method(type, "UpdateStateIdle");
        }

        private static bool Prefix(object __instance, float deltaTime)
        {
            return !ProductivityTweaksService.TryHandleNurseAssistedCleanup(__instance, deltaTime);
        }
    }

    [HarmonyPatch]
    internal static class FlexibleStretcherPickupPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.MapScriptInterface");
            return type == null ? null : AccessTools.Method(type, "FindClosestObjectWithTag");
        }

        private static void Postfix(
            object position,
            int floorIndex,
            object department,
            string tag,
            object accessRights,
            bool needsToBeFree,
            bool onlyComposite,
            ref Lopital.TileObject __result)
        {
            __result = (Lopital.TileObject)ProductivityTweaksService.TryFindFlexibleTransportObject(__result, position, floorIndex, department, tag, accessRights, needsToBeFree, onlyComposite);
        }
    }

    [HarmonyPatch]
    internal static class ProductivityFreeTimeAvailabilityPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("Lopital.ProcedureComponent");
            if (type == null)
            {
                yield break;
            }

            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if ((method.Name == "GetProcedureAvailability" || method.Name == "GetProcedureAvailabilty")
                    && method.ReturnType.FullName == "Lopital.ProcedureSceneAvailability")
                {
                    yield return method;
                }
            }
        }

        private static void Postfix(object[] __args, ref Lopital.ProcedureSceneAvailability __result)
        {
            if (__result != Lopital.ProcedureSceneAvailability.AVAILABLE)
            {
                return;
            }

            var procedure = __args == null ? null : __args.OfType<GameDBProcedure>().FirstOrDefault();
            var department = __args == null ? null : __args.OfType<Lopital.Department>().FirstOrDefault();
            if (ProductivityTweaksService.ShouldSuppressFreeTime(procedure, department))
            {
                __result = Lopital.ProcedureSceneAvailability.STAFF_BUSY;
            }
        }
    }

    [HarmonyPatch]
    internal static class ProductivityEmergencySpeedPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var yielded = new HashSet<MethodBase>();
            foreach (var typeName in new[] { "Lopital.Behavior", "Lopital.BehaviorNurse", "Lopital.BehaviorDoctor", "Lopital.BehaviorLabSpecialist" })
            {
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, "GetSpeedModifier");
                if (method != null && yielded.Add(method))
                {
                    yield return method;
                }
            }
        }

        private static void Postfix(object __instance, ref float __result)
        {
            // Do not modify GetSpeedModifier: the game uses it for animation speed,
            // while running movement uses WalkComponent.UpdateMovement directly.
        }
    }

    [HarmonyPatch]
    internal static class ProductivityEmergencyRunningMovementPatch
    {
        private static MethodBase TargetMethod()
        {
            var walkType = AccessTools.TypeByName("Lopital.WalkComponent");
            var floorType = AccessTools.TypeByName("Lopital.Floor");
            return walkType == null || floorType == null
                ? null
                : AccessTools.Method(walkType, "UpdateMovement", new[] { floorType, typeof(float) });
        }

        private static void Prefix(object __instance, ref float deltaTime)
        {
            if (ProductivityTweaksService.ShouldBoostRunningMovement(__instance))
            {
                deltaTime *= Math.Max(1f, RuntimeSettings.Config.EmergencyRunSpeedMultiplier.Value);
                RuntimeCounters.EmergencySpeedBoosts++;
            }
        }
    }

    [HarmonyPatch]
    internal static class ProductivityChainedHospitalizedExaminationPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("Lopital.HospitalizationComponent");
            return type == null ? null : AccessTools.Method(type, "UpdateStateAfterExaminationCheck");
        }

        private static bool Prefix(object __instance, float deltaTime)
        {
            return !ProductivityTweaksService.TryHandleAfterExaminationCheck(__instance, deltaTime);
        }
    }
}
