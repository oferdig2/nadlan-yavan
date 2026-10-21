-- 016: Map drop (owner, 2026-10-20). Drone / phone media dropped on the map land unassigned (attached to "MapDrop",
-- attached_to_id = the uploader's user id), are inspected (EXIF / DJI XMP / MP4 header / .srt flight track), and are
-- then assigned to a Parcel by an Admin or Data person - or deleted (by hand, or 5 days after upload).
-- A drone video with a flight track can cover several Parcels: one row per Parcel and time frame in file_segment.

ALTER TABLE file_attachment DROP CHECK ck_file_attachment_target;

ALTER TABLE file_attachment
  ADD CONSTRAINT ck_file_attachment_target CHECK (attached_to_type IN ('Parcel', 'Asset', 'Portfolio', 'Contact', 'User', 'MapDrop'));

-- The .srt next to a DJI video: one line per frame with time, position and height.
INSERT INTO file_type (code, name, category, sort_order)
SELECT 'DRONE_TRACK', 'Drone flight track (.srt)', 'Marketing', 45
WHERE NOT EXISTS (SELECT 1 FROM file_type WHERE code = 'DRONE_TRACK');

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'MAP_DROP', 'Map drop', 'Drop photos, videos and flight tracks on the map, then assign them to Parcels (also files someone else dropped).', 'Role', NULL, 'Files', 480
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'MAP_DROP');

INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code = 'MAP_DROP'
WHERE r.code = 'DATA_PERSON'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);

-- What was read from inside a file (the browser reads it at upload; nothing is guessed). One row per file, any target.
CREATE TABLE file_media_metadata (
  file_attachment_id BIGINT NOT NULL,
  taken_local VARCHAR(19) NULL COMMENT 'camera clock, yyyy-MM-dd HH:mm:ss, no time zone (EXIF DateTimeOriginal / MP4 / first .srt line)',
  camera_make VARCHAR(60) NULL,
  camera_model VARCHAR(80) NULL,
  latitude DOUBLE NULL,
  longitude DOUBLE NULL,
  rel_altitude_m DECIMAL(9,2) NULL COMMENT 'height above the take-off point (DJI)',
  abs_altitude_m DECIMAL(9,2) NULL,
  gimbal_yaw DECIMAL(6,2) NULL,
  gimbal_pitch DECIMAL(6,2) NULL COMMENT '-90 = straight down, 0 = horizon',
  gps_status VARCHAR(20) NULL COMMENT 'as the camera wrote it, e.g. DJI "Invalid" (unreliable: seen right and 24 km off)',
  duration_s DECIMAL(10,3) NULL,
  width INT NULL,
  height INT NULL,
  bitrate_kbps INT NULL,
  video_codec VARCHAR(20) NULL,
  index_at_end TINYINT(1) NULL COMMENT 'MP4 index (moov) after the data: slow start when streaming',
  source_modified_utc DATETIME(3) NULL COMMENT 'the original file''s last-modified time, from the browser',
  track_file_id BIGINT NULL COMMENT 'the .srt file this video''s track came from',
  track_json MEDIUMTEXT NULL COMMENT 'flight track, 2 points a second: [[ms, lat, lng, relAlt], ...]',
  suggestions_json TEXT NULL COMMENT 'Parcels found from the position / track when inspected',
  read_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (file_attachment_id),
  CONSTRAINT fk_file_media_metadata_file FOREIGN KEY (file_attachment_id) REFERENCES file_attachment (file_attachment_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A video shown on other Parcels too, each with its time frames (e.g. 1:30-1:55 and 2:14-2:50). Its home Parcel's
-- frames are rows here as well. Playback on a Parcel starts at that Parcel's frame.
CREATE TABLE file_segment (
  file_segment_id BIGINT NOT NULL AUTO_INCREMENT,
  file_attachment_id BIGINT NOT NULL,
  parcel_id BIGINT NOT NULL,
  start_ms INT NOT NULL,
  end_ms INT NOT NULL,
  source VARCHAR(10) NOT NULL DEFAULT 'Manual' COMMENT 'Track = suggested from the flight track and confirmed, Manual = typed',
  updated_by_user_id BIGINT NULL,
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (file_segment_id),
  KEY ix_file_segment_file (file_attachment_id, start_ms),
  KEY ix_file_segment_parcel (parcel_id),
  CONSTRAINT fk_file_segment_file FOREIGN KEY (file_attachment_id) REFERENCES file_attachment (file_attachment_id) ON DELETE CASCADE,
  CONSTRAINT fk_file_segment_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id) ON DELETE CASCADE,
  CONSTRAINT ck_file_segment_times CHECK (start_ms >= 0 AND end_ms > start_ms),
  CONSTRAINT ck_file_segment_source CHECK (source IN ('Track', 'Manual'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
