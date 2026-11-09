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
internal enum MotorMode { ReadOnly, Armed, Targeting, Resuming }
