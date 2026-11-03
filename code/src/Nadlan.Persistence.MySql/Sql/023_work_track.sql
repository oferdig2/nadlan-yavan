-- 023: the contractor's track (owner, 2026-11-03), seen by Admins only. Recorded on the phone only while "Work on parcel"
-- is On: Start / Stop / out of the parcel / back in / 1 km away (the track ends) as events, and between them a point
-- every 5 s at most, none within 2 m of the last one. The phone keeps what isn't sent yet and sends it about every
-- minute as a batch; each batch is added to the end of the parcel's one path - the same path day after day, so the
-- times of work, inside and outside the parcel all come from it.
-- work_location (one row per point, migration 021) is no longer written or read; it stays (migrations never drop).

CREATE TABLE work_track (
  work_track_id BIGINT NOT NULL AUTO_INCREMENT,
  work_request_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL COMMENT 'no foreign key: the path stays when the parcel leaves the request',
  path LONGTEXT NOT NULL COMMENT 'JSON [{t (ms since 1970, UTC), e (start|stop|out|in|far; none = a point), lat, lng, acc (m), out (1 = outside the parcel)}], in the order received',
  first_utc DATETIME(3) NOT NULL,
  last_utc DATETIME(3) NOT NULL,
  entry_count INT NOT NULL,
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_track_id),
  UNIQUE KEY ux_work_track_parcel (work_request_id, parcel_id),
  KEY ix_work_track_last (last_utc),
  CONSTRAINT fk_work_track_request FOREIGN KEY (work_request_id) REFERENCES work_request (work_request_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The batches received: the phone sends one again when it got no answer, and it is added once.
CREATE TABLE work_track_batch (
  work_request_id BIGINT NOT NULL,
  batch_id VARCHAR(40) NOT NULL,
  received_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (work_request_id, batch_id),
  KEY ix_work_track_batch_received (received_utc),
  CONSTRAINT fk_work_track_batch_request FOREIGN KEY (work_request_id) REFERENCES work_request (work_request_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
