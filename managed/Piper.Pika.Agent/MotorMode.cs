namespace LuckyRobots.Piper.Pika.Agent;

// Pika-E agent motor mode: the commanded intent. Measured power is the separate
// `enabled` feedback; Following and Holding are PikaFollowSession sub-states.
//   ReadOnly ──enable──▶ Armed                 (a repeated enable keeps Targeting)
//   Armed ──position──▶ Targeting ──measured arrival or hold──▶ Armed
//   Armed ──follow-start──▶ Armed + follow Following ──hold──▶ Armed + follow Holding
//   ReadOnly ──resume-hold──▶ Resuming ──verified──▶ Armed | ──failed or cancelled──▶ ReadOnly
//   any ──Disable (operator, watchdog, disconnect, agent exit)──▶ ReadOnly
// Invariants: Targeting ⇒ no follow session; a Following or Holding follow
// session ⇒ Armed; Resuming ⇔ the measured-hold resume is pending.
//
// Why four modes, not the eight in target-architecture (Pika-E):
// - EnableRequested and Enabled are Armed split by measured feedback. Every
//   motor admission (position, follow-start, follow-update) already requires
//   the measured `enabled`, so a mode would copy that feedback into commanded
//   intent and could drift from it when the driver drops enable on its own.
// - Targeting and Resuming are modes here already.
// - Following and Holding are PikaFollowSession phases, which own their
//   target, cadence and deadline; Armed is their only legal motor mode.
// - Disabling has no duration in the agent: Disable() writes the disable
//   packet and enters ReadOnly in the same step. The wait for disabled
//   feedback is PikaPowerFeedback, a display acknowledgement that gates
//   nothing; power evidence is judged by the host from fresh feedback.
internal enum MotorMode { ReadOnly, Armed, Targeting, Resuming }
