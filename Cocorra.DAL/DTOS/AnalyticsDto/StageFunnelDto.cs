namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// One step of the stage participation funnel (AN-027 / M-400).
    ///
    /// The distinction between <see cref="Count"/> and <see cref="ObservedParticipations"/> is
    /// the whole point of this shape and must survive any refactor:
    ///
    ///   Count                   — sequential. Null when this step, or ANY step before it, is
    ///                             uninstrumented, because a sequential count through a missing
    ///                             step is not a low number, it is an unknown one.
    ///   ObservedParticipations  — this step alone, ignoring order. Available whenever the event
    ///                             type is instrumented at all.
    ///
    /// Returning 0 for an uninstrumented step would read as "nobody raised their hand": a
    /// confident, plausible, false conclusion, and precisely the failure this programme exists
    /// to prevent.
    /// </summary>
    public class StageFunnelStepDto
    {
        /// <summary>Human-readable step name, e.g. "Joined room".</summary>
        public string Step { get; set; } = string.Empty;

        /// <summary>The underlying event type, so a reader can trace the number to its source.</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>
        /// False when the backing event type has never been recorded at or before the end of
        /// the requested window. A false value here must render as a labelled gap, never as 0.
        /// </summary>
        public bool IsMeasured { get; set; }

        /// <summary>
        /// True when the event type began emitting partway through the requested window, so the
        /// step is measured for only part of the period and is not comparable to the steps
        /// around it. Renders as a boundary marker on a trend.
        /// </summary>
        public bool IsPartiallyMeasured { get; set; }

        /// <summary>
        /// First time this event type was ever recorded. Null when never recorded. A client
        /// draws the start-of-data boundary here.
        /// </summary>
        public DateTime? MeasurementStartedUtc { get; set; }

        /// <summary>
        /// Why this step carries no sequential count. Null when the step is fully counted.
        /// Populated so the UI has text to render in the gap rather than inventing its own.
        /// </summary>
        public string? NotMeasuredReason { get; set; }

        /// <summary>
        /// Distinct (room, participant) pairs that reached this step with every earlier step
        /// already completed at an earlier-or-equal time. Null when not computable.
        /// </summary>
        public int? Count { get; set; }

        /// <summary>Distinct participants behind <see cref="Count"/>. Null when not computable.</summary>
        public int? DistinctUsers { get; set; }

        /// <summary>
        /// Distinct (room, participant) pairs with at least one event of this type in the
        /// window, ignoring sequence entirely. Present whenever the step is instrumented, which
        /// is what lets a partially instrumented funnel still show real numbers where it has
        /// them instead of collapsing to nothing.
        /// </summary>
        public int? ObservedParticipations { get; set; }

        /// <summary>
        /// Raw event count for this step, including repeats. A participant who raises, lowers
        /// and raises again contributes 1 to <see cref="Count"/> and 2 here: the funnel measures
        /// people, this measures demand, and collapsing them would understate one or the other.
        /// </summary>
        public int? TotalEvents { get; set; }

        /// <summary>Share of the first step's sequential population that reached this step.</summary>
        public double? ConversionFromFirstStepPercent { get; set; }

        /// <summary>Share of the immediately preceding step that reached this step.</summary>
        public double? ConversionFromPreviousStepPercent { get; set; }

        /// <summary>Sequential drop between the previous step and this one, in participations.</summary>
        public int? DropOffFromPreviousStep { get; set; }

        /// <summary>Share of the previous step lost at this transition.</summary>
        public double? DropOffFromPreviousStepPercent { get; set; }

        /// <summary>
        /// Median seconds from the previous step to this one. Median rather than mean: the
        /// hand-raise wait is gated on a human host noticing, so the distribution is long-tailed
        /// and a mean would describe nobody.
        /// </summary>
        public double? MedianSecondsFromPreviousStep { get; set; }

        /// <summary>90th percentile of the same gap — where the tail actually sits.</summary>
        public double? P90SecondsFromPreviousStep { get; set; }
    }

    /// <summary>
    /// AN-027 / M-400 — the stage participation funnel, scoped per (room, participant).
    ///
    /// The unit is a participation, not a user: the same person joining two rooms and raising a
    /// hand in one of them is two participations with different outcomes, and collapsing them to
    /// a user would hide exactly the room-level variation this funnel exists to locate.
    /// </summary>
    public class StageFunnelDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public IEnumerable<StageFunnelStepDto> Steps { get; set; } = [];

        /// <summary>
        /// True only when every one of the four steps is instrumented for the whole window.
        /// A client should render the funnel as diagnostic only when this is true.
        /// </summary>
        public bool IsFullyInstrumented { get; set; }

        /// <summary>
        /// Distinct rooms contributing at least one non-host participation to the funnel.
        /// Context for reading the absolute numbers: 40 promotions across 2 rooms and across
        /// 40 rooms are different findings.
        /// </summary>
        public int RoomsInScope { get; set; }

        /// <summary>
        /// Participations promoted to the stage with no preceding hand raise in that room —
        /// a direct host invitation.
        ///
        /// Reported separately because a strict sequential funnel drops these at step 2, which
        /// would otherwise read as promotions vanishing. Null when either event is uninstrumented.
        /// </summary>
        public int? DirectPromotionsWithoutHandRaise { get; set; }

        /// <summary>
        /// Earliest event timestamp backing this funnel, bounded by the 180-day raw retention
        /// window. Null when the funnel has no data at all.
        /// </summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
