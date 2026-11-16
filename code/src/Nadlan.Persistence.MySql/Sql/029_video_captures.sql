-- 029: "Catch image" (owner, 2026-11-16): while a video's original still exists, an Admin / Data person stops on a moment in the
-- Hi-res player and the worker cuts that exact frame from the original at full size; it becomes a photo of the video's own parcel /
-- asset. Our server only records the request (it never opens a video). New table only; no data changes.

CREATE TABLE video_capture (
  capture_id BIGINT NOT NULL AUTO_INCREMENT,
  file_attachment_id BIGINT NOT NULL COMMENT 'the video',
  at_ms INT NOT NULL COMMENT 'the moment in the video, in ms',
  file_type_id INT NOT NULL COMMENT 'the photo type it becomes (Drone photo for a drone video, else Photo)',
  requested_by_user_id BIGINT NULL,
  status VARCHAR(20) NOT NULL DEFAULT 'Pending' COMMENT 'Pending | Running | Done | Failed',
  job_id VARCHAR(200) NULL COMMENT 'the worker: Fargate task ARN',
  photo_file_id BIGINT NULL COMMENT 'the photo made from it',
  error_message VARCHAR(1000) NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  done_utc DATETIME(3) NULL,
  PRIMARY KEY (capture_id),
  KEY ix_video_capture_file (file_attachment_id, created_utc),
  KEY ix_video_capture_status (status, created_utc),
  KEY ix_video_capture_job (job_id),
  CONSTRAINT fk_video_capture_file FOREIGN KEY (file_attachment_id) REFERENCES file_attachment (file_attachment_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
