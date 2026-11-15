-- 028: large videos made light (owner, 2026-11-10/15): AWS MediaConvert writes a 720p and a 1080p MP4 (index first) and a
-- frame every 2 s under _renditions/{file}/; the 1080p becomes the kept file once the original is deleted (after a few days).
-- One row per video; it goes with its file. New table only; no data changes.

CREATE TABLE video_rendition (
  file_attachment_id BIGINT NOT NULL,
  status VARCHAR(20) NOT NULL DEFAULT 'Queued' COMMENT 'Queued | Submitted | Ready | Failed',
  job_id VARCHAR(64) NULL COMMENT 'MediaConvert job id',
  attempts INT NOT NULL DEFAULT 0,
  with_audio TINYINT(1) NOT NULL DEFAULT 1 COMMENT '0 = the video has no sound track (drone videos): no audio output',
  queued_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  submitted_utc DATETIME(3) NULL,
  completed_utc DATETIME(3) NULL COMMENT 'outputs verified (exist, not empty)',
  error_message VARCHAR(1000) NULL,
  small_key VARCHAR(500) NULL COMMENT '720p MP4, relative to the storage root',
  small_size BIGINT NULL,
  large_key VARCHAR(500) NULL COMMENT '1080p MP4: the kept file once the original is deleted',
  large_size BIGINT NULL,
  duration_ms INT NULL,
  frame_count INT NULL COMMENT 'frames captured, one every 2 s',
  frames_json JSON NULL COMMENT 'frame numbers offered as photos under the video',
  poster_index INT NULL,
  heights_json JSON NULL COMMENT '[[ms, metres above take-off], ...] from the DJI flight track, at each half-metre change',
  takeoff_offset_m DECIMAL(6,1) NOT NULL DEFAULT 0 COMMENT 'added to every height: "take-off was 5 m above the plot"',
  original_key VARCHAR(500) NULL COMMENT 'the uploaded file, kept until original_deleted_utc',
  original_size BIGINT NULL,
  original_mime VARCHAR(100) NULL,
  original_deleted_utc DATETIME(3) NULL,
  frames_deleted_utc DATETIME(3) NULL,
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (file_attachment_id),
  KEY ix_video_rendition_status (status, queued_utc),
  KEY ix_video_rendition_completed (completed_utc),
  CONSTRAINT fk_video_rendition_file FOREIGN KEY (file_attachment_id) REFERENCES file_attachment (file_attachment_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Each run of the conversion worker (ffmpeg on AWS Fargate, started only while a video is converted): when, how big, how long,
-- what it cost - for the Admin (owner, 2026-11-15: "register the times of operation of this worker + costs so Alon can know").
CREATE TABLE video_worker_run (
  job_id VARCHAR(200) NOT NULL COMMENT 'the Fargate task ARN',
  file_attachment_id BIGINT NULL COMMENT 'the video (NULL once the file is deleted: the run and its cost stay)',
  file_name VARCHAR(255) NULL,
  submitted_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  started_utc DATETIME(3) NULL COMMENT 'billing start: the image pull',
  stopped_utc DATETIME(3) NULL,
  cpu INT NOT NULL COMMENT 'vCPU units, 1024 = 1 vCPU',
  memory_mb INT NOT NULL,
  storage_gb INT NOT NULL,
  status VARCHAR(40) NOT NULL DEFAULT 'RUNNING',
  stopped_by VARCHAR(200) NULL COMMENT 'finished | failed | checker: ...',
  cost_usd DECIMAL(10,5) NULL COMMENT 'at the list prices in the settings',
  PRIMARY KEY (job_id),
  KEY ix_video_worker_run_submitted (submitted_utc),
  KEY ix_video_worker_run_open (stopped_by, submitted_utc),
  CONSTRAINT fk_video_worker_run_file FOREIGN KEY (file_attachment_id) REFERENCES file_attachment (file_attachment_id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
