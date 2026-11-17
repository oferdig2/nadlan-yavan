-- 030: originals kept 7 days, and an Admin / Data person can keep one longer, 7 days at a time (owner, 2026-11-17: "show the
-- time left and add an extend button of 7 more days"). One new column, empty for every existing row; no data changes.

ALTER TABLE video_rendition
  ADD COLUMN original_keep_until_utc DATETIME(3) NULL COMMENT 'set by Extend: the original is deleted after this instead of completed + the days in the settings' AFTER original_deleted_utc;
