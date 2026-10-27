-- 019: distance to the sea (owner, 2026-10-27). Adds only.
-- A beach is a Parcel carrying an Asset of property type BEACH (found by this code, never by its name). The distance
-- of a Parcel to the sea = to the nearest beach polygon + that beach's depth; it is calculated when a card opens and
-- kept here, one row per Parcel.

INSERT INTO property_type (code, name, sort_order)
SELECT 'BEACH', 'Beach', 900
WHERE NOT EXISTS (SELECT 1 FROM property_type WHERE code = 'BEACH');

-- A row is fresh while the beaches are as they were (beach_version, see SeaDistanceSql) and the Parcel hasn't changed
-- since (computed_utc >= parcel.updated_utc). Stale rows are recalculated at the next card open; lists only read fresh ones.
CREATE TABLE parcel_sea_distance (
  parcel_id BIGINT NOT NULL,
  distance_m INT NULL COMMENT 'to the sea, metres; NULL = no beach within search_m',
  beach_asset_id BIGINT NULL COMMENT 'the nearest beach Asset (for checking; never sent to browsers)',
  search_m INT NOT NULL COMMENT 'the search cap it was calculated with',
  beach_version VARCHAR(100) NOT NULL COMMENT 'the beaches as they were then (count | id sum | last change)',
  computed_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (parcel_id),
  CONSTRAINT fk_parcel_sea_distance_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
