// The two map views of the customer presentation, behind one interface:
//   create2dView - the satellite map seen from above: where the presentation starts and tours (Alon, 2026-11-05)
//   create3dView - Google's photorealistic 3D (Maps JavaScript "maps3d": Map3DElement), on the "3D" button
// Both: { showAll(): Promise, fly(i): Promise, orbit(i, ms): Promise, stop(), highlight(i), showMe(position, fly): Promise }.
// Items come from GET /api/presentation: { polygons: [GeoJSON Polygon], statusColor, ... }.
(function (window) {
  "use strict";

  var Nadlan = window.Nadlan = window.Nadlan || {};

  function safeColor(c) { return /^#[0-9a-fA-F]{6}$/.test(c || "") ? c : "#f59e0b"; }

  function rgba(hex, alpha) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + (n >> 16) + "," + (n >> 8 & 255) + "," + (n & 255) + "," + alpha + ")";
  }

  function boundsOf(polygons) {
    var b = { west: 180, south: 90, east: -180, north: -90 };
    polygons.forEach(function (g) {
      g.coordinates[0].forEach(function (p) {
        b.west = Math.min(b.west, p[0]); b.east = Math.max(b.east, p[0]);
        b.south = Math.min(b.south, p[1]); b.north = Math.max(b.north, p[1]);
      });
    });
    return b;
  }

  function allBounds(items) {
    return boundsOf(items.reduce(function (all, it) { return all.concat(it.polygons); }, []));
  }

  function metresBetween(a, b) {
    var lat = (a.lat + b.lat) / 2 * Math.PI / 180;
    var dx = (b.lng - a.lng) * 111320 * Math.cos(lat), dy = (b.lat - a.lat) * 110540;
    return Math.sqrt(dx * dx + dy * dy);
  }

  // Properties within this distance of one already in the group join it ("a cluster").
  var CLUSTER_LINK_M = 3000;
  // The overview never zooms out further than this box (diagonal): far wider, the 3D view takes too long to draw.
  var MAX_OVERVIEW_M = 12000;

  /**
   * What "All" and the first view show (owner, 2026-11-05): property 1 and the ones near it - each within 3 km of another
   * in the group - never zoomed out past ~12 km. Properties far away are reached with the strip, ◀ ▶ or the tour.
   */
  function overviewBounds(items) {
    var centres = items.map(function (it) { return centerOf(boundsOf(it.polygons)); });
    var inGroup = [0];
    for (var grew = true; grew;) {
      grew = false;
      centres.forEach(function (c, i) {
        if (inGroup.indexOf(i) < 0 && inGroup.some(function (j) { return metresBetween(c, centres[j]) <= CLUSTER_LINK_M; })) { inGroup.push(i); grew = true; }
      });
    }
    var b = allBounds(inGroup.map(function (i) { return items[i]; }));
    var size = diagonalMetres(b);
    if (size <= MAX_OVERVIEW_M) { return b; }
    // One long chain of neighbours: the part of it around property 1.
    var k = MAX_OVERVIEW_M / size, c = centres[0];
    return { west: c.lng - (c.lng - b.west) * k, east: c.lng + (b.east - c.lng) * k, south: c.lat - (c.lat - b.south) * k, north: c.lat + (b.north - c.lat) * k };
  }

  // Size of a box in metres (good enough at these scales).
  function diagonalMetres(b) {
    var lat = (b.south + b.north) / 2 * Math.PI / 180;
    var dx = (b.east - b.west) * 111320 * Math.cos(lat), dy = (b.north - b.south) * 110540;
    return Math.sqrt(dx * dx + dy * dy);
  }

  function centerOf(b) { return { lat: (b.south + b.north) / 2, lng: (b.west + b.east) / 2 }; }

  // A camera that frames the box, looking down at 60° from the south-west-ish (heading 30°), aimed at the ground's
  // height there. Its altitude is above sea level: aimed at 0, a hillside plot was viewed from under the ground (owner,
  // 2026-11-05). ground = null (height unknown): higher and steeper, so even a hill of ~1,000 m stays below the camera.
  function cameraFor(b, wide, ground) {
    var c = centerOf(b);
    var range = Math.min(60000, Math.max(wide ? 600 : 220, diagonalMetres(b) * (wide ? 1.6 : 3.2)));
    if (ground === null || ground === undefined) {
      return { center: { lat: c.lat, lng: c.lng, altitude: 0 }, range: Math.max(range, wide ? 3000 : 1500), tilt: wide ? 35 : 40, heading: 30 };
    }
    return { center: { lat: c.lat, lng: c.lng, altitude: Math.max(0, ground) }, range: range, tilt: wide ? 50 : 62, heading: 30 };
  }

  // The ground's height (m above sea level) at each point, from Google's Elevation service; null for all when it isn't
  // allowed for the key (then cameraFor keeps a safe height) or doesn't answer in time.
  function groundHeights(points) {
    var none = points.map(function () { return null; });
    if (!points.length || !google.maps.importLibrary) { return Promise.resolve(none); }
    var asked = google.maps.importLibrary("elevation").then(function (lib) {
      return new lib.ElevationService().getElevationForLocations({ locations: points });
    }).then(function (r) {
      return points.map(function (p, i) { var x = r && r.results && r.results[i]; return x && isFinite(x.elevation) ? x.elevation : null; });
    }, function () { return none; });
    return Promise.race([asked, new Promise(function (r) { setTimeout(function () { r(none); }, 5000); })]);
  }

  function ring(coords) { return coords.map(function (p) { return { lat: p[1], lng: p[0] }; }); }

  // Resolves when the map reports the animation ended, or after the expected time (the event isn't guaranteed).
  function animation(target, start, ms) {
    return new Promise(function (resolve) {
      var done = false;
      function end() {
        if (done) { return; }
        done = true;
        target.removeEventListener("gmp-animationend", end);
        resolve();
      }
      target.addEventListener("gmp-animationend", end);
      setTimeout(end, ms + 2000);
      try { start(); } catch (e) { end(); }
    });
  }

  /**
   * @param {object} lib    google.maps.importLibrary("maps3d")
   * @param {HTMLElement} el
   * @param {Array} items
   * @param {{ onPick: function(number): void }} options
   */
  function create3dView(lib, el, items, options) {
    // Ground heights: the properties' centres, then the whole set's centre. Until they arrive (or without them) the
    // cameras keep a safe height.
    var ground = items.map(function () { return null; }), groundAll = null;
    var overviewBox = overviewBounds(items);
    var overview = cameraFor(overviewBox, true, null);
    var heightsReady = groundHeights(items.map(function (it) { return centerOf(boundsOf(it.polygons)); }).concat([centerOf(overviewBox)]))
      .then(function (h) { ground = h.slice(0, items.length); groundAll = h[items.length]; overview = cameraFor(overviewBox, true, groundAll); });
    var map = new lib.Map3DElement({ center: overview.center, range: overview.range, tilt: overview.tilt, heading: overview.heading });
    if (lib.MapMode && "mode" in map) { map.mode = lib.MapMode.HYBRID; } // place names and roads over the 3D imagery
    map.classList.add("pres-map-3d");
    // Google's own buttons (map-type switch, compass) off: our bar does the job (owner, 2026-11-05).
    if ("defaultUIHidden" in map) { map.defaultUIHidden = true; } else { map.setAttribute("default-ui-hidden", ""); }
    el.appendChild(map);

    var clampToGround = lib.AltitudeMode ? lib.AltitudeMode.CLAMP_TO_GROUND : "CLAMP_TO_GROUND";
    var relativeToGround = lib.AltitudeMode ? lib.AltitudeMode.RELATIVE_TO_GROUND : "RELATIVE_TO_GROUND";
    var Polygon = lib.Polygon3DInteractiveElement || lib.Polygon3DElement;
    var Marker = lib.Marker3DInteractiveElement || lib.Marker3DElement;
    var shapes = [];

    items.forEach(function (it, i) {
      var color = safeColor(it.statusColor);
      var mine = [];
      it.polygons.forEach(function (g) {
        var poly = new Polygon({ strokeColor: color, strokeWidth: 4, fillColor: rgba(color, 0.3), altitudeMode: clampToGround, drawsOccludedSegments: true });
        // The property names changed during Google's beta (outer/innerCoordinates -> path/innerPaths): set what exists.
        if ("path" in poly) { poly.path = ring(g.coordinates[0]); } else { poly.outerCoordinates = ring(g.coordinates[0]); }
        if (g.coordinates.length > 1) {
          var holes = g.coordinates.slice(1).map(ring);
          if ("innerPaths" in poly) { poly.innerPaths = holes; } else { poly.innerCoordinates = holes; }
        }
        if (lib.Polygon3DInteractiveElement) { poly.addEventListener("gmp-click", function () { options.onPick(i); }); }
        map.append(poly);
        mine.push({ poly: poly, color: color });
      });

      // A numbered pin above the Parcel, so it can be found (and clicked) from far away.
      var c = centerOf(boundsOf(it.polygons));
      var marker = new Marker({ position: { lat: c.lat, lng: c.lng, altitude: 35 }, altitudeMode: relativeToGround, extruded: true, label: String(i + 1) });
      if (lib.Marker3DInteractiveElement) { marker.addEventListener("gmp-click", function () { options.onPick(i); }); }
      map.append(marker);
      shapes.push(mine);
    });

    function orbitOptions(camera, ms) {
      return { camera: camera, durationMillis: ms, repeatCount: 1 };
    }

    var me = null; // "you are here" pin
    var MePin = lib.Marker3DElement || lib.Marker3DInteractiveElement;

    // Loading the 3D library is not enough: without graphics acceleration, with the Map Tiles API off on the key, or
    // with its quota used up, the element stays black. Only a fully drawn view ("steady") proves it works - a "not
    // steady yet" can come from a globe that will never draw.
    var steady = false, failed = false, onErrors = [];
    map.addEventListener("gmp-steadychange", function (e) {
      if (e.isSteady || (e.detail && e.detail.isSteady)) { steady = true; }
    });
    map.addEventListener("gmp-error", function () {
      failed = true;
      onErrors.forEach(function (f) { f(); });
    });

    return {
      kind: "3d",
      element: map,
      /**
       * True once the 3D map has drawn a view; false on an error, or when it hasn't within ms. Only time while the
       * tab is visible counts: a background tab doesn't draw, which says nothing about 3D working.
       */
      ready: function (ms) {
        return new Promise(function (resolve) {
          var waited = 0;
          (function poll() {
            if (failed) { resolve(false); return; }
            if (steady) { resolve(true); return; }
            if (!document.hidden) { waited += 250; }
            if (waited > ms) { resolve(false); return; }
            setTimeout(poll, 250);
          })();
        });
      },
      /** An error after it worked (e.g. quota used up mid-meeting): the page then switches to the satellite map. */
      onError: function (f) { onErrors.push(f); },
      destroy: function () { onErrors = []; map.remove(); },
      /** The viewer's own position (my-location.js); fly = also bring the camera there. */
      showMe: function (p, fly) {
        var position = { lat: p.lat, lng: p.lng, altitude: 3 };
        if (!me) {
          me = new MePin({ position: position, altitudeMode: relativeToGround, extruded: true, label: "You" });
          map.append(me);
        } else {
          me.position = position;
        }
        if (!fly) { return Promise.resolve(); }
        return groundHeights([{ lat: p.lat, lng: p.lng }]).then(function (h) {
          var known = h[0] !== null;
          var cam = { center: { lat: p.lat, lng: p.lng, altitude: known ? Math.max(0, h[0]) : 0 },
            range: Math.min(20000, Math.max(known ? 350 : 1500, p.accuracy * 4)), tilt: known ? 60 : 40, heading: 30 };
          return animation(map, function () { map.flyCameraTo({ endCamera: cam, durationMillis: 2500 }); }, 2500);
        });
      },
      showAll: function () {
        return heightsReady.then(function () {
          return animation(map, function () { map.flyCameraTo({ endCamera: overview, durationMillis: 3000 }); }, 3000);
        });
      },
      fly: function (i) {
        return heightsReady.then(function () {
          var cam = cameraFor(boundsOf(items[i].polygons), false, ground[i]);
          return animation(map, function () { map.flyCameraTo({ endCamera: cam, durationMillis: 3500 }); }, 3500);
        });
      },
      orbit: function (i, ms) {
        var cam = cameraFor(boundsOf(items[i].polygons), false, ground[i]);
        return animation(map, function () {
          try { map.flyCameraAround(orbitOptions(cam, ms)); } catch (e) { map.flyCameraAround({ camera: cam, durationMillis: ms, rounds: 1 }); } // older beta
        }, ms);
      },
      stop: function () { if (map.stopCameraAnimation) { map.stopCameraAnimation(); } },
      highlight: function (index) {
        shapes.forEach(function (mine, i) {
          mine.forEach(function (s) {
            s.poly.strokeWidth = i === index ? 7 : 4;
            s.poly.fillColor = rgba(s.color, i === index ? 0.45 : 0.25);
          });
        });
      }
    };
  }

  /**
   * The satellite map from above (google.maps, already loaded): same interface, no 3D flight.
   * options.titleOf(item, index): the pin's hover text.
   * The map gets its own box: Google writes "position: relative" onto the element it is given, which took the full-screen
   * stage's "position: fixed" away - the stage fell to 0 px high and the map was drawn into nothing (review 2026-11-05).
   */
  function create2dView(el, items, options) {
    var box = document.createElement("div");
    box.className = "pres-map-2d";
    el.appendChild(box);
    var map = new google.maps.Map(box, { mapTypeId: "hybrid", streetViewControl: false, fullscreenControl: false, gestureHandling: "greedy",
      mapTypeControl: false, rotateControl: false, clickableIcons: false });
    var layer = new google.maps.Data({ map: map });
    var highlighted = -1;
    var measuring = false, markers = [];
    items.forEach(function (it, i) {
      it.polygons.forEach(function (g) {
        layer.add(new google.maps.Data.Feature({ geometry: new google.maps.Data.Polygon(g.coordinates.map(ring)), properties: { index: i, color: safeColor(it.statusColor) } }));
      });
      var c = centerOf(boundsOf(it.polygons));
      var marker = new google.maps.Marker({ map: map, position: c, label: { text: String(i + 1), color: "#fff", fontWeight: "700" },
        title: options.titleOf ? options.titleOf(it, i) : "" });
      marker.addListener("click", function () { options.onPick(i); });
      markers.push(marker);
    });
    layer.setStyle(function (f) {
      var on = f.getProperty("index") === highlighted;
      return { strokeColor: f.getProperty("color"), strokeWeight: on ? 5 : 3, fillColor: f.getProperty("color"), fillOpacity: on ? 0.45 : 0.25,
        clickable: !measuring };
    });

    // The ruler (owner, 2026-11-05: "from the beach, or from anything he wants"): map-measure.js, as on the main map.
    // While it is on, a click on a property adds a point instead of opening its card.
    var ruler = null;
    if (Nadlan.mapMeasure) {
      ruler = Nadlan.mapMeasure.createMeasureControl(map, {
        setStatus: options.setStatus || function () { },
        button: options.measureButton || null, // the top bar's, left of "All" (owner, 2026-11-05)
        position: "RIGHT_CENTER",
        onActiveChange: function (on) {
          measuring = on;
          layer.setStyle(layer.getStyle());
          markers.forEach(function (m) { m.setClickable(!on); });
          if (options.onMeasuring) { options.onMeasuring(on); }
        }
      });
    }
    layer.addListener("click", function (e) { options.onPick(e.feature.getProperty("index")); });

    // margin: around the property; options.covered() = what the page's card and bars cover ({ top, right, bottom, left }
    // px) - the property is centred in what stays visible (owner, 2026-11-08: on a phone the card hid the polygon).
    function fit(b, margin) {
      var c = options.covered ? options.covered() : { top: 0, right: 0, bottom: 0, left: 0 };
      var el = map.getDiv(), w = el.clientWidth, h = el.clientHeight;
      var m = Math.max(16, Math.min(margin, (w - c.left - c.right) / 4, (h - c.top - c.bottom) / 4));
      var padding = { top: c.top + m, right: c.right + m, bottom: c.bottom + m, left: c.left + m };
      if (w - padding.left - padding.right < 60 || h - padding.top - padding.bottom < 60) { padding = margin; } // too little left: plain
      map.fitBounds(new google.maps.LatLngBounds({ lat: b.south, lng: b.west }, { lat: b.north, lng: b.east }), padding);
      return new Promise(function (r) { setTimeout(r, 1200); });
    }
    fit(overviewBounds(items), 80);

    var dot = null, accuracyRing = null; // not "ring": that name is the helper above (a var here would hide it)

    return {
      kind: "2d",
      ready: function () { return Promise.resolve(true); },
      destroy: function () { if (ruler) { ruler.destroy(); } google.maps.event.clearInstanceListeners(map); box.remove(); },
      showMe: function (p, fly) {
        var here = { lat: p.lat, lng: p.lng };
        if (!dot) {
          accuracyRing = new google.maps.Circle({ map: map, center: here, radius: p.accuracy, clickable: false,
            strokeColor: "#2563eb", strokeOpacity: 0.5, strokeWeight: 1, fillColor: "#3b82f6", fillOpacity: 0.15 });
          dot = new google.maps.Marker({ map: map, position: here, clickable: false, zIndex: 9999, title: "You are here",
            icon: { path: google.maps.SymbolPath.CIRCLE, scale: 8, fillColor: "#2563eb", fillOpacity: 1, strokeColor: "#ffffff", strokeWeight: 3 } });
        } else {
          dot.setPosition(here);
          accuracyRing.setCenter(here);
          accuracyRing.setRadius(p.accuracy);
        }
        if (!fly) { return Promise.resolve(); }
        if (p.accuracy > 150) { map.fitBounds(accuracyRing.getBounds()); } else { map.panTo(here); if (map.getZoom() < 18) { map.setZoom(18); } }
        return new Promise(function (r) { setTimeout(r, 1200); });
      },
      showAll: function () { return fit(overviewBounds(items), 80); },
      fly: function (i) {
        // At most zoom 18: closer, rural imagery is often missing or loads in patches.
        // The cap inside the fit (maxZoom), so the property stays centred in the visible part; the presenter may zoom closer after.
        map.setOptions({ maxZoom: 18 });
        return fit(boundsOf(items[i].polygons), 160).then(function () { map.setOptions({ maxZoom: null }); });
      },
      orbit: function (i, ms) { return new Promise(function (r) { setTimeout(r, Math.min(ms, 6000)); }); }, // no flight in 2D: a pause
      stop: function () { },
      highlight: function (index) { highlighted = index; layer.setStyle(layer.getStyle()); }
    };
  }

  Nadlan.presentViews = { create3dView: create3dView, create2dView: create2dView };
})(window);
