-- 008: Small preview images for photos and videos. The browser makes a ~480 px JPEG when it uploads the file and stores
-- it next to the original (<storage_key>.thumb.jpg); cards show it instead of downloading the full-size original.
-- Files from before this (or uploaded by a browser that couldn't decode the format) have none and fall back to the original.

ALTER TABLE file_attachment
  ADD COLUMN has_thumbnail TINYINT(1) NOT NULL DEFAULT 0 COMMENT '1 = <storage_key>.thumb.jpg exists' AFTER sort_order;
