// Admin page, Costs tab: what GreekPlot pays Amazon and Google for, the free allowances and unit prices, and a calculator
// (stored GB, video viewers, website visits, map users, server size -> a monthly bill). Prices are public list prices,
// kept here in PRICES with the date they were read; nothing is fetched or saved. "Copy for AI" puts the prices, the
// inputs and the result on the clipboard as plain text, to ask an AI about other scenarios.
(function (window, $) {
  "use strict";

  var Nadlan = window.Nadlan;
  var HOURS = 730; // hours in an average month

  // Read 2026-10-20 from the AWS Price List API (region EU Stockholm, CloudFront Europe) and the price pages below.
  var PRICES = {
    asOf: "2026-10-20",
    sources: [
      ["AWS Price List API (eu-north-1, CloudFront Europe)", "https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/price-changes.html"],
      ["Amazon CloudFront pay-as-you-go pricing", "https://aws.amazon.com/cloudfront/pricing/pay-as-you-go/"],
      ["Amazon CloudFront flat-rate plans", "https://aws.amazon.com/cloudfront/pricing/"],
      ["Amazon EC2 on-demand pricing (100 GB free data out)", "https://aws.amazon.com/ec2/pricing/on-demand/"],
      ["Google Maps Platform pricing (updated 2026-10-16)", "https://developers.google.com/maps/billing-and-pricing/pricing"]
    ],
    servers: [ // EC2 on-demand Linux, EU (Stockholm), USD per hour
      { type: "t3.micro", hour: 0.0108, spec: "2 vCPU (burst), 1 GB RAM - today" },
      { type: "t3.small", hour: 0.0216, spec: "2 vCPU (burst), 2 GB RAM" },
      { type: "t3.medium", hour: 0.0432, spec: "2 vCPU (burst), 4 GB RAM" },
      { type: "t3.large", hour: 0.0864, spec: "2 vCPU (burst), 8 GB RAM" },
      { type: "t3.xlarge", hour: 0.1728, spec: "4 vCPU (burst), 16 GB RAM" }
    ],
    ebsGbMonth: 0.0836,       // gp3 disk, per GB-month (3,000 IOPS and 125 MB/s included)
    snapshotGbMonth: 0.0475,  // disk snapshots (daily backups of the disk), per GB-month
    ipv4Hour: 0.005,          // the server's fixed public IPv4 address
    s3GbMonth: 0.023,         // S3 Standard, first 50 TB
    s3PutPer1k: 0.005,        // uploads (PUT/POST/LIST)
    s3GetPer1k: 0.0004,       // reads (GET) - the CDN reads S3 when its cache doesn't have a file
    internetFreeGb: 100,      // data out to the internet free every month, all AWS services together
    internetTiers: [[10240, 0.09], [51200, 0.085], [153600, 0.07], [Infinity, 0.05]], // per GB, after the free 100 GB
    cfFreeGb: 1024,           // CloudFront always-free tier, every month
    cfFreeRequests: 10000000,
    cfTiers: [[10240, 0.085], [51200, 0.08], [153600, 0.06], [512000, 0.04], [Infinity, 0.03]], // Europe, per GB
    cfHttpsPer10k: 0.012,
    cfFunctionFree: 2000000,  // our function that blocks internal paths runs on every request
    cfFunctionPer1M: 0.10,
    cfPlans: [ // flat-rate alternative to pay-as-you-go (no overage charges)
      { name: "Free", month: 0, gb: 100, requests: 1000000 },
      { name: "Pro", month: 15, gb: 51200, requests: 10000000 },
      { name: "Business", month: 200, gb: 51200, requests: 125000000 },
      { name: "Premium", month: 1000, gb: 51200, requests: 500000000 }
    ],
    // Google: per 1,000 events by monthly volume; the first events of each month are free.
    mapsDynamic: { free: 10000, tiers: [[100000, 7], [500000, 5.6], [1000000, 4.2], [5000000, 2.1], [Infinity, 0.53]] },
    maps3d: { free: 1000, tiers: [[100000, 6], [500000, 5.1], [1000000, 4.2], [5000000, 3.3], [Infinity, 2.4]] }
  };

  // Today's usage (AWS, read 2026-10-20 09:27 UTC) as the starting values.
  var DEFAULTS = {
    storedGb: 1.8, uploadsPerMonth: 200, viewers: 50, videosPerViewer: 3, videoMb: 250, photosPerViewer: 20, photoMb: 8,
    visits: 500, mbPerVisit: 3, mapUsers: 10, mapOpensPerUser: 100, presentations: 50,
    server: "t3.micro", diskGb: 20, snapshotGb: 10, domainYear: 0
  };
  var PRESET_500 = { viewers: 500, videosPerViewer: 3, videoMb: 250, photosPerViewer: 20, photoMb: 8 };

  function money(v) { return "$" + (v < 10 ? v.toFixed(2) : Math.round(v).toLocaleString("en-US")); }
  function num(v, d) { return Number(v).toLocaleString("en-US", { maximumFractionDigits: d === undefined ? 0 : d }); }
  function gbText(gb) { return gb >= 1024 ? num(gb / 1024, 2) + " TB" : num(gb, gb < 10 ? 2 : 0) + " GB"; }

  // Volume after the free amount, priced by tiers of the TOTAL monthly volume [upTo, price per unit].
  function tiered(total, free, tiers, perUnits) {
    var cost = 0, from = 0;
    tiers.forEach(function (t) {
      var lo = Math.max(from, free), hi = Math.min(total, t[0]);
      if (hi > lo) { cost += (hi - lo) * t[1] / perUnits; }
      from = t[0];
    });
    return cost;
  }
  // Same, but the free amount comes off the bottom of the tiers (AWS: the first 100 GB / 1 TB are free, then tier 1 starts).
  function afterFree(total, free, tiers) {
    var billable = Math.max(0, total - free), cost = 0, from = 0;
    tiers.forEach(function (t) {
      var hi = Math.min(billable, t[0]);
      if (hi > from) { cost += (hi - from) * t[1]; }
      from = t[0];
    });
    return cost;
  }

  function calculate(i) {
    var P = PRICES, lines = [];
    function line(group, item, quantity, free, cost, note) { lines.push({ group: group, item: item, quantity: quantity, free: free, cost: cost, note: note || "" }); }

    var videoMb = i.videoMb;
    var cdnGb = i.viewers * (i.videosPerViewer * videoMb + i.photosPerViewer * i.photoMb) / 1024;
    // A video plays as a run of range requests (~1 per 2 MB); a photo is 2 (photo + thumbnail).
    var cdnRequests = i.viewers * (i.videosPerViewer * Math.max(1, videoMb / 2) + i.photosPerViewer * 2);
    var webGb = i.visits * i.mbPerVisit / 1024;
    var mapLoads = i.mapUsers * i.mapOpensPerUser;
    var server = P.servers.filter(function (s) { return s.type === i.server; })[0] || P.servers[0];

    line("Server", "EC2 " + server.type + " (" + server.spec + ")", HOURS + " hours", "-", server.hour * HOURS);
    line("Server", "Disk " + i.diskGb + " GB (gp3)", i.diskGb + " GB", "-", i.diskGb * P.ebsGbMonth);
    line("Server", "Disk snapshots (backups)", i.snapshotGb + " GB", "-", i.snapshotGb * P.snapshotGbMonth);
    line("Server", "Fixed public IP address", HOURS + " hours", "-", P.ipv4Hour * HOURS);
    line("Server", "Website traffic (pages, map data)", gbText(webGb), "100 GB (all AWS)", afterFree(webGb, P.internetFreeGb, P.internetTiers),
      "Free 100 GB a month across all AWS services");

    line("Files", "S3 storage (photos, videos, documents, backups)", gbText(i.storedGb), "-", i.storedGb * P.s3GbMonth);
    line("Files", "S3 uploads", num(i.uploadsPerMonth) + " files", "-", i.uploadsPerMonth * 3 * P.s3PutPer1k / 1000, "~3 requests per uploaded file");
    var cacheMisses = cdnRequests * 0.1; // the CDN reads S3 only when a file isn't in its cache yet
    line("Files", "S3 reads by the CDN", num(cacheMisses) + " requests", "-", cacheMisses * P.s3GetPer1k / 1000, "Assumes 10% cache misses; S3 to CDN traffic is free");

    var cfData = afterFree(cdnGb, P.cfFreeGb, P.cfTiers);
    var cfReq = Math.max(0, cdnRequests - P.cfFreeRequests) * P.cfHttpsPer10k / 10000;
    var cfFn = Math.max(0, cdnRequests - P.cfFunctionFree) * P.cfFunctionPer1M / 1000000;
    line("CDN (streaming)", "Video and photo traffic", gbText(cdnGb), "1 TB", cfData);
    line("CDN (streaming)", "Requests", num(cdnRequests), "10 million", cfReq);
    line("CDN (streaming)", "Path-check function", num(cdnRequests), "2 million", cfFn);
    var plan = P.cfPlans.filter(function (p) { return cdnGb <= p.gb && cdnRequests <= p.requests; })[0];
    var payg = cfData + cfReq + cfFn;

    line("Google Maps", "Map loads (Dynamic Maps)", num(mapLoads), "10,000", tiered(mapLoads, P.mapsDynamic.free, P.mapsDynamic.tiers, 1000));
    line("Google Maps", "3D presentations (Photorealistic 3D)", num(i.presentations), "1,000", tiered(i.presentations, P.maps3d.free, P.maps3d.tiers, 1000),
      "Assumes one billable 3D event per presentation opened");
    if (i.domainYear > 0) { line("Server", "Domain greekplot.com", "1 month", "-", i.domainYear / 12); }

    var total = lines.reduce(function (s, l) { return s + l.cost; }, 0);
    return { lines: lines, total: total, cdnGb: cdnGb, cdnRequests: cdnRequests, webGb: webGb, mapLoads: mapLoads, plan: plan, payg: payg, videoMb: videoMb };
  }

  var FIELDS = [
    ["Storage", [
      ["storedGb", "Files stored (GB)", "Photos, videos, documents and backups in S3. Today about 1.8 GB."],
      ["uploadsPerMonth", "Files uploaded per month", ""]]],
    ["Videos and photos watched (through the CDN)", [
      ["viewers", "People viewing per month", "Customers and staff opening presentations or files."],
      ["videosPerViewer", "Videos each person watches", ""],
      ["videoMb", "Average video size (MB)", "Alon's drone videos: 100-340 MB each (about 36 Mbit/s)."],
      ["photosPerViewer", "Photos each person opens", ""],
      ["photoMb", "Average photo size (MB)", "Drone photos: 5-10 MB."]]],
    ["Website and maps", [
      ["visits", "Website visits per month", "Each time someone opens GreekPlot."],
      ["mbPerVisit", "MB per visit", "Pages and map data from our server (not the map images, which come from Google)."],
      ["mapUsers", "People using the map", ""],
      ["mapOpensPerUser", "Map opens per person per month", "Each page load of the map counts once."],
      ["presentations", "3D presentations opened per month", ""]]],
    ["Server (change only with many users)", [
      ["server", "Server size", "t3.micro is enough for a team. Bigger only when many people use GreekPlot at the same time; videos and photos don't load the server.", "server"],
      ["diskGb", "Server disk (GB)", "Today 20 GB."],
      ["snapshotGb", "Disk snapshots kept (GB)", "Daily disk backups; only changes are stored. Estimated."],
      ["domainYear", "Domain per year (USD)", "From the GoDaddy invoice, if you want it included."]]]
  ];

  function priceTable() {
    var P = PRICES;
    var rows = [
      ["Server (EC2 t3.micro)", "Runs GreekPlot and its database", "-", "$" + P.servers[0].hour + " per hour (~" + money(P.servers[0].hour * HOURS) + "/month)", "", "", "", "", ""],
      ["Server disk (EBS gp3)", "The server's 20 GB disk", "-", "$" + P.ebsGbMonth + " per GB-month", "", "", money(P.ebsGbMonth), money(P.ebsGbMonth * 100), money(P.ebsGbMonth * 1024)],
      ["Disk snapshots", "Daily disk backups", "-", "$" + P.snapshotGbMonth + " per GB-month", "", "", money(P.snapshotGbMonth), money(P.snapshotGbMonth * 100), money(P.snapshotGbMonth * 1024)],
      ["Public IP address", "greekplot.com points to it", "-", "$" + P.ipv4Hour + " per hour (~" + money(P.ipv4Hour * HOURS) + "/month)", "", "", "", "", ""],
      ["File storage (S3)", "Photos, videos, documents, backups", "-", "$" + P.s3GbMonth + " per GB-month", "", "", money(P.s3GbMonth), money(P.s3GbMonth * 100), money(P.s3GbMonth * 1024)],
      ["S3 uploads (PUT)", "Each uploaded file (~3 requests)", "-", "$" + P.s3PutPer1k + " per 1,000", money(P.s3PutPer1k), money(P.s3PutPer1k * 10), "", "", ""],
      ["S3 reads (GET)", "The CDN fetching a file it hasn't cached", "-", "$" + P.s3GetPer1k + " per 1,000", money(P.s3GetPer1k), money(P.s3GetPer1k * 10), "", "", ""],
      ["Internet traffic from AWS", "Website pages and data", "100 GB / month", "$0.09 per GB (first 10 TB)", "", "", money(0.09), money(0.09 * 100), money(0.09 * 1024)],
      ["CDN traffic (CloudFront, Europe)", "Videos and photos to viewers", "1 TB / month", "$0.085 per GB (first 10 TB)", "", "", money(0.085), money(0.085 * 100), money(0.085 * 1024)],
      ["CDN requests", "Each photo, each piece of a video", "10 million / month", "$" + P.cfHttpsPer10k + " per 10,000", money(P.cfHttpsPer10k / 10), money(P.cfHttpsPer10k), "", "", ""],
      ["CDN function", "Blocks internal folders on every request", "2 million / month", "$0.10 per million", money(0.0001), money(0.001), "", "", ""],
      ["Google map loads (Dynamic Maps)", "Each time the map page opens", "10,000 / month", "$7 per 1,000 (to 100K), then less", money(7), money(70), "", "", ""],
      ["Google 3D (Photorealistic 3D)", "3D presentation", "1,000 / month", "$6 per 1,000 (to 100K), then less", money(6), money(60), "", "", ""],
      ["Google sign-in", "Sign in with Google", "Always free", "$0", "", "", "", "", ""],
      ["HTTPS certificate (Let's Encrypt)", "The padlock for greekplot.com", "Always free", "$0", "", "", "", "", ""]
    ];
    var $t = $("<table class=\"meta-table costs-prices\"><thead><tr><th>Service</th><th>What it's for</th><th>Free every month</th><th>Price after that</th>" +
      "<th>per 1K</th><th>per 10K</th><th>per 1 GB</th><th>per 100 GB</th><th>per 1 TB</th></tr></thead><tbody></tbody></table>");
    rows.forEach(function (r) {
      var $tr = $("<tr></tr>");
      r.forEach(function (c, k) { $tr.append($(k >= 4 ? "<td class=\"num\"></td>" : "<td></td>").text(c)); });
      $t.find("tbody").append($tr);
    });
    return $t;
  }

  function aiText(i, r) {
    var P = PRICES, out = [];
    out.push("GreekPlot monthly running costs (prices as of " + P.asOf + ", USD, AWS region EU Stockholm, CloudFront Europe).");
    out.push("");
    out.push("PRICES");
    out.push("- Server EC2 on-demand per hour: " + P.servers.map(function (s) { return s.type + " $" + s.hour; }).join(", ") + " (730 hours/month).");
    out.push("- Disk gp3 $" + P.ebsGbMonth + "/GB-month; snapshots $" + P.snapshotGbMonth + "/GB-month; public IPv4 $" + P.ipv4Hour + "/hour.");
    out.push("- S3 storage $" + P.s3GbMonth + "/GB-month; uploads $" + P.s3PutPer1k + " per 1,000 requests; reads $" + P.s3GetPer1k + " per 1,000.");
    out.push("- Internet data out from AWS: first 100 GB/month free (all services), then $0.09/GB first 10 TB, $0.085 next 40 TB.");
    out.push("- CloudFront pay-as-you-go: always free 1 TB data out, 10 million HTTPS requests and 2 million function runs per month; then Europe $0.085/GB first 10 TB, $0.08 next 40 TB, $0.06 next 100 TB; HTTPS $0.012 per 10,000 requests; functions $0.10 per million. S3 to CloudFront traffic is free.");
    out.push("- CloudFront flat-rate plans (no overage charges): " + P.cfPlans.map(function (p) { return p.name + " $" + p.month + "/month (" + gbText(p.gb) + ", " + num(p.requests) + " requests)"; }).join("; ") + ".");
    out.push("- Google Maps Dynamic Maps: first 10,000 loads/month free, then per 1,000: $7 to 100K, $5.60 to 500K, $4.20 to 1M, $2.10 to 5M, $0.53 above.");
    out.push("- Google Photorealistic 3D: first 1,000/month free, then per 1,000: $6 to 100K, $5.10 to 500K, $4.20 to 1M.");
    out.push("- Free: Google sign-in, Let's Encrypt certificate.");
    out.push("");
    out.push("MY INPUTS");
    FIELDS.forEach(function (g) { g[1].forEach(function (f) { out.push("- " + f[1] + ": " + (f[3] === "checkbox" ? (i[f[0]] ? "yes" : "no") : i[f[0]])); }); });
    out.push("");
    out.push("RESULT (my calculator)");
    r.lines.forEach(function (l) { out.push("- " + l.group + " / " + l.item + ": " + l.quantity + " -> " + money(l.cost)); });
    out.push("- TOTAL per month: " + money(r.total));
    out.push("");
    out.push("Question: (write your question here, e.g. what does it cost if 500 people each watch 3 of my drone videos?)");
    return out.join("\n");
  }

  Nadlan.initializeAdminCosts = function ($panel) {
    var P = PRICES;
    $panel.append($("<div class=\"meta-toolbar\"><h3>Costs</h3></div>").append($("<span class=\"muted\"></span>")
      .text("What GreekPlot pays Amazon and Google, and a calculator for any scenario. List prices as of " + P.asOf + ", USD.")));

    // Three sections that fold: Prices (closed), Your scenario (open), Cost table (closed); the total stays in view.
    function section(title, open) {
      var $d = $("<details class=\"costs-section\"></details>").prop("open", open).append($("<summary></summary>").text(title));
      $panel.append($d);
      return $d;
    }
    section("Prices", false).append(priceTable(),
      $("<p class=\"muted small\"></p>").text("CloudFront also offers flat-rate plans instead of paying per GB: " +
        P.cfPlans.map(function (p) { return p.name + " $" + p.month + "/month (" + gbText(p.gb) + ", " + num(p.requests) + " requests)"; }).join(" · ") +
        ". Google 3D and map loads get cheaper per 1,000 at higher volumes (shown in Copy for AI)."),
      $("<p class=\"muted small\"></p>").append("Sources: ",
        P.sources.map(function (s, k) { return $("<span></span>").append(k ? " · " : "", $("<a target=\"_blank\" rel=\"noopener\"></a>").attr("href", s[1]).text(s[0])); })));

    var $form = $("<form class=\"costs-form\" autocomplete=\"off\"></form>");
    FIELDS.forEach(function (g) {
      var $fs = $("<fieldset></fieldset>").append($("<legend></legend>").text(g[0]));
      g[1].forEach(function (f) {
        var $input;
        if (f[3] === "checkbox") {
          $input = $("<input type=\"checkbox\">").attr("name", f[0]).prop("checked", !!DEFAULTS[f[0]]);
          $fs.append($("<label class=\"costs-check\"></label>").append($input, " ", document.createTextNode(f[1])), f[2] ? $("<div class=\"muted small\"></div>").text(f[2]) : $());
          return;
        }
        if (f[3] === "server") {
          $input = $("<select class=\"input\"></select>").attr("name", f[0]);
          P.servers.forEach(function (s) {
            $input.append($("<option></option>").val(s.type).text(s.type + " - " + s.spec + " - " + money(s.hour * HOURS) + "/month"));
          });
          $input.val(DEFAULTS[f[0]]);
        } else {
          $input = $("<input class=\"input\" type=\"number\" min=\"0\" step=\"any\">").attr("name", f[0]).val(DEFAULTS[f[0]]);
        }
        $fs.append($("<label></label>").append($("<span></span>").text(f[1]), $input), f[2] ? $("<div class=\"muted small\"></div>").text(f[2]) : $());
      });
      $form.append($fs);
    });

    var $buttons = $("<div class=\"btn-row\"></div>").append(
      $("<button type=\"button\" class=\"btn\" data-act=\"preset\">Example: 500 people watch 3 drone videos each</button>"),
      $("<button type=\"button\" class=\"btn\" data-act=\"reset\">Back to today's usage</button>"),
      $("<button type=\"button\" class=\"btn btn-primary\" data-act=\"copy\">Copy for AI</button>"),
      $("<span class=\"muted small\" data-role=\"copied\" hidden>Copied - paste it into any AI chat and add your question.</span>"));
    section("Your scenario", true).append($form, $buttons);
    var $total = $("<section class=\"costs-summary\"></section>").appendTo($panel);
    var $result = $("<div class=\"costs-result\"></div>");
    section("Cost table", false).append($result);

    function inputs() {
      var i = {};
      FIELDS.forEach(function (g) {
        g[1].forEach(function (f) {
          var $el = $form.find("[name=" + f[0] + "]");
          i[f[0]] = f[3] === "checkbox" ? $el.prop("checked") : f[3] === "server" ? $el.val() : Math.max(0, Number($el.val()) || 0);
        });
      });
      return i;
    }

    function render() {
      var i = inputs(), r = calculate(i);
      // Subtotal per group, in the order the groups first appear (Server, Files, CDN (streaming), Google Maps).
      var groups = [];
      r.lines.forEach(function (l) {
        var g = groups.filter(function (x) { return x.name === l.group; })[0];
        if (!g) { g = { name: l.group, lines: [], total: 0 }; groups.push(g); }
        g.lines.push(l);
        g.total += l.cost;
      });

      var $t = $("<table class=\"meta-table\"><thead><tr><th>Item</th><th>Amount</th><th>Free</th><th class=\"num\">Per month</th></tr></thead><tbody></tbody></table>");
      groups.forEach(function (g) {
        g.lines.forEach(function (l) {
          $t.find("tbody").append($("<tr></tr>").attr("title", l.note).append(
            $("<td></td>").text(l.item), $("<td></td>").text(l.quantity), $("<td></td>").text(l.free), $("<td class=\"num\"></td>").text(money(l.cost))));
        });
        $t.find("tbody").append($("<tr class=\"costs-subtotal\"></tr>").append(
          $("<td colspan=\"3\"></td>").text(g.name), $("<td class=\"num\"></td>").text(money(g.total))));
      });
      $t.append($("<tfoot></tfoot>").append($("<tr class=\"costs-total\"></tr>").append(
        $("<td colspan=\"3\"></td>").text("Total per month"), $("<td class=\"num\"></td>").text(money(r.total)))));
      $result.empty().append($t, $("<p class=\"muted small\"></p>").text("Hover a line for its assumption."));

      // Always in view: the total, the subtotals, and the CDN plan hint.
      $total.empty().append(
        $("<div class=\"costs-total-line\"></div>").append($("<span></span>").text("Total per month"),
          $("<strong></strong>").text(money(r.total)), $("<span class=\"muted\"></span>").text("about " + money(r.total * 12) + " a year")),
        $("<div class=\"costs-groups\"></div>").append(groups.map(function (g) {
          return $("<span></span>").append(document.createTextNode(g.name + " "), $("<b></b>").text(money(g.total)));
        })),
        $("<p class=\"small\"></p>").text("Streaming: " + gbText(r.cdnGb) + " and " + num(r.cdnRequests) + " requests a month; pay-as-you-go " +
          money(r.payg) + "/month" + (r.plan ? ", or the CloudFront flat-rate " + r.plan.name + " plan at " + money(r.plan.month) + "/month." : "; above every flat-rate plan's limits.")),
        $("<p class=\"muted small\"></p>").text("Estimates from list prices; the AWS and Google invoices are the real numbers. Taxes not included."));
    }

    $form.on("input change", render);
    $buttons.on("click", "[data-act]", function () {
      var act = $(this).data("act");
      if (act === "preset" || act === "reset") {
        var values = act === "preset" ? $.extend({}, DEFAULTS, PRESET_500) : DEFAULTS;
        Object.keys(values).forEach(function (k) {
          var $el = $form.find("[name=" + k + "]");
          if ($el.is(":checkbox")) { $el.prop("checked", !!values[k]); } else { $el.val(values[k]); }
        });
        render();
      } else if (act === "copy") {
        copy(aiText(inputs(), calculate(inputs())));
      }
    });

    // Clipboard; if the browser refuses, the text opens in a box to select and copy by hand.
    function copy(text) {
      var shown = function () { $buttons.find("[data-role=copied]").prop("hidden", false); };
      var byHand = function () {
        var $text = $("<textarea class=\"input\" rows=\"18\" readonly style=\"width:100%\"></textarea>").val(text);
        var d = Nadlan.dialog.open({ title: "Copy for AI", width: 640, content: $text,
          buttons: [{ text: "Close", click: function () { d.close(); } }] });
        $text.trigger("select");
      };
      if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(text).then(shown, byHand); } else { byHand(); }
    }
    render();
  };

  // For tests and the AI text: the pure calculation.
  Nadlan.adminCosts = { calculate: calculate, PRICES: PRICES, DEFAULTS: DEFAULTS };
})(window, jQuery);
