-- 017: a long, low flight over many small Parcels can give more Parcel suggestions than TEXT (64 KB) holds - the save
-- then failed (review of the map-drop branch, 2026-10-21). MEDIUMTEXT holds 16 MB; the app also keeps at most 500.

ALTER TABLE file_media_metadata MODIFY suggestions_json MEDIUMTEXT NULL COMMENT 'Parcels found from the position / track when inspected';
