    char lane_device[160], message[256], phase[32], error[256], owner[81];
    enum operation op;
    unsigned generation, request_id, enable_count;
    unsigned enable_cursor;
    bool enable_requested[6], enable_acknowledged[6];
    double enable_first_sent_at[6], enable_last_sent_at[6], enable_baseline[6];
    unsigned disable_cursor;
    bool disable_requested[6], disable_acknowledged[6];
    double disable_first_sent_at[6], disable_last_sent_at[6];
    bool pose_tx_valid;
    bool position_seed_pending;
    unsigned pose_tx_generation, pose_tx_speed;
    uint8_t pose_tx_mode;
    double pose_tx_at;
    int32_t pose_tx_millidegrees[6];
    double started, heartbeat, last_tick, sent_at, settled_at, progress_at, best_error, final_sent_at;
    double target[6], command[6], initial[6], speed, tolerance, gripper_target, gripper_command;
    double gripper_effort;
    uint8_t gripper_max_mm;
    bool gripper_entry;
    bool synchronized_pose;
    bool range_followup;
    double range_followup_target[6], range_followup_speed, range_followup_percent;
    bool arrival_hold;
    double arrival_hold_at, arrival_hold_pose[6];
    bool following, follow_committed;
    /* Opt-in simulation follower: wire-quantized reference intervals are
       bounded by this connection's reported speed and acceleration limits. */
    bool trajectory_follow;
    /* Pinned wire transport survives a measured follow-end hold. No idle
       mode switching: a subsequent ordinary action admits PV only after its
       existing stationary gate. */
    bool streamed_position_mode;
    double trajectory_velocity[6], trajectory_previous_dt;
    double trajectory_speed[6], trajectory_acceleration[6];
    double trajectory_minimum[6], trajectory_maximum[6];
    double follow_updated;
    unsigned follow_sequence;
    unsigned cycle_stage;
    unsigned teach_return_stage;
    /* Volatile evidence from this connection, never restored from a session
       file. A new STOP, disconnect, fault or playback invalidates the cycle. */
    char teaching_cycle_device[160];
    unsigned teaching_cycle_generation;
    double teaching_cycle_seen, teaching_cycle_completed;
    bool recovering_at_pose;
    double leg_start[6], leg_span, leg_fraction, pause_until;
    bool query, disable_keeps_recovery;
    unsigned query_joint, query_generation;
    double query_start, query_next;
    double auto_reconnect_at, auto_query_at;
    struct obstruction_watch obstruction[6];
    unsigned obstruction_joint;
    double speed_percent;
    bool stop_delivered;
