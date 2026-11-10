    char lane_device[160], message[256], phase[32], error[256], owner[81];
    enum operation op;
    enum lane_stage stage;
    enum pose_phase pose_phase;
    unsigned generation, request_id, enable_count;
    /* Per-driver power handshake for the active ENABLE or DISABLE. */
    unsigned power_cursor;
    bool power_requested[6], power_acknowledged[6];
    double power_first_sent_at[6], power_last_sent_at[6], enable_baseline[6];
    bool pose_tx_valid;
    unsigned pose_tx_generation, pose_tx_speed;
    uint8_t pose_tx_mode;
    double pose_tx_at;
    int32_t pose_tx_millidegrees[6];
    /* Measurement timestamps, 0 while unset. lane_check() requires both to be
       0 at OP_IDLE. */
    double started, heartbeat, last_tick, sent_at, settled_at, progress_at, best_error, final_sent_at;
    double target[6], command[6], initial[6], speed, tolerance, gripper_target, gripper_command;
    double gripper_effort;
    uint8_t gripper_max_mm;
    bool gripper_entry;
    /* STAGE_ALIGN_FOLLOWUP payload: the destination after verified range entry. */
    double range_followup_target[6], range_followup_speed, range_followup_percent;
    /* POSE_ARRIVAL_HOLD payload. */
    double arrival_hold_at, arrival_hold_pose[6];
    enum follow_phase follow;
    /* STAGE_MOVE_TRAJECTORY payload. Opt-in simulation follower: wire-quantized
       reference intervals are bounded by this connection's reported speed and
       acceleration limits. */
    double trajectory_velocity[6], trajectory_previous_dt;
    double trajectory_speed[6], trajectory_acceleration[6];
    double trajectory_minimum[6], trajectory_maximum[6];
    double follow_updated;
    unsigned follow_sequence;
    /* Volatile evidence from this connection, never restored from a session
       file. A new STOP, disconnect, fault or playback invalidates the cycle. */
    enum teach_cycle teaching_cycle;
    unsigned teaching_cycle_generation;
    double teaching_cycle_seen, teaching_cycle_completed;
    /* Seamless TEACH_RETURN payload: this handoff is recover-at-pose. */
    bool recovering_at_pose;
    double leg_start[6], leg_span, leg_fraction, pause_until;
    enum query_phase query;
    bool disable_keeps_recovery;
    /* INITIAL: next limit request 0..11. RETRY: rotating request 0..13. */
    unsigned query_cursor, query_generation;
    double query_start, query_next;
    double auto_reconnect_at, auto_query_at;
    struct obstruction_watch obstruction[6];
    unsigned obstruction_joint;
    double speed_percent;
    bool stop_delivered;
