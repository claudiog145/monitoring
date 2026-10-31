namespace JourneyMonitor.Core.Incidents;

/// <summary>
/// Maps an exception URL or operation name onto the journey step used by the domain KQL.
/// Stage labels match the journey waterfall (for example "01 Login", "09 Payment").
/// </summary>
public static class JourneyStageResolver
{
    public const string Unclassified = "Unclassified";

    private static readonly HashSet<string> KnownStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "01 Login",
        "01 T-Mobile SSO",
        "01 Flight Search",
        "02 Hotel Search",
        "02 Car Search",
        "02 Cruise Listing",
        "02 Review Your Trip",
        "03 Hotel Results",
        "03 Car Results",
        "03 Cruise Detail",
        "03 Add Traveler Info",
        "04 Hotel Details",
        "04 Rooms & Rates",
        "04 Choose Your Seat",
        "05 PreBook",
        "05 Total Price",
        "05 Review & Pay",
        "06 Checkout",
        "06 Cabin Selection",
        "06 Confirmation",
        "07 Booking",
        "07 Passenger",
        "08 Confirmation",
        "08 Options",
        "09 Cancellation",
        "09 Payment",
        "10 Review",
        "11 Confirmation",
    };

    /// <summary>Journey step for the exception table. Never the App Insights role name.</summary>
    public static string ForException(string? domain, string? url, string? operation)
    {
        var stage = Resolve(domain, url, operation);
        if (stage is not null) return stage;

        var named = (operation ?? "").Trim();
        return KnownStages.Contains(named) ? named : Unclassified;
    }

    public static string? Resolve(string? domain, string? url, string? operation)
    {
        var text = $"{url} {operation}";
        return domain?.Trim().ToLowerInvariant() switch
        {
            "hotels" => Stay(text, hotel: true),
            "cars" => Stay(text, hotel: false),
            "air" => Air(text),
            "cruises" => Cruise(text),
            _ => null,
        };
    }

    /// <summary>Hotels and cars share the stays host. Markers follow the v6 journey queries.</summary>
    private static string? Stay(string text, bool hotel)
    {
        if (Has(text, "login.json") || Has(text, "logincheck")) return "01 Login";

        if (hotel)
        {
            if (Has(text, "BackgroundHotelSearch")) return "03 Hotel Results";
            if (Has(text, "search.json")) return "02 Hotel Search";
            if (Has(text, "searchrooms") || Has(text, "getcancelpolicy") || Has(text, "HotelDetails")
                || Has(text, "ZentrumHubHotelDetailsContent") || Has(text, "GetReviews"))
            {
                return "04 Hotel Details";
            }

            if (Has(text, "book.json")) return "05 PreBook";
        }
        else
        {
            // BackgroundCarSearch contains CarSearch, so it has to win first.
            if (Has(text, "BackgroundCarSearch")) return "03 Car Results";
            if (Has(text, "CarLocationSearch") || Has(text, "searchlocations") || Has(text, "CarSearch"))
            {
                return "02 Car Search";
            }

            if (Has(text, "/cars/")) return "03 Car Results";
            if (Has(text, "CarBooking")) return "05 PreBook";
        }

        // PurchaseComplete contains Purchase. CompletePurchase contains Purchase.
        if (Has(text, "PurchaseComplete")) return "08 Confirmation";
        if (Has(text, "CompletePurchase") || Has(text, "determine3dseligibility")) return "07 Booking";

        if (Has(text, "/reservations/Purchase") || Has(text, "userpeople_read")
            || Has(text, "AddRemoveInsurance") || Has(text, "TravelerProfile"))
        {
            return "06 Checkout";
        }

        if (Has(text, "/reservations/Cancel")) return "09 Cancellation";
        // PayGate and payment pages sit on the booking step in the stays journey.
        if (Has(text, "payment") || Has(text, "PayGate")) return "07 Booking";
        return null;
    }

    /// <summary>Same first-match order as the air journey query's NameText / OpText case.</summary>
    private static string? Air(string text)
    {
        if (Has(text, "confirmation") || Has(text, "getbooking") || Has(text, "bookingretrieve")
            || Has(text, "sendemail"))
        {
            return "06 Confirmation";
        }

        if (Has(text, "seatmap") || Has(text, "seat map") || Has(text, "seatselection"))
        {
            return "04 Choose Your Seat";
        }

        if (Has(text, "traveler") || Has(text, "passenger")) return "03 Add Traveler Info";

        if (Has(text, "review and pay") || Has(text, "review & pay") || Has(text, "book")
            || Has(text, "payment") || Has(text, "checkout") || Has(text, "insurance")
            || Has(text, "loyaltypoint") || Has(text, "redemptionrate") || Has(text, "earnrate")
            || Has(text, "calculatefee") || Has(text, "supervisorvalidation"))
        {
            return "05 Review & Pay";
        }

        if (Has(text, "pricecheck") || Has(text, "price check") || Has(text, "farerules")
            || Has(text, "fare rules") || Has(text, "getcontent") || Has(text, "itineraryreview")
            || Has(text, "review"))
        {
            return "02 Review Your Trip";
        }

        if (Has(text, "search") || Has(text, "bfm") || Has(text, "flightsearch") || Has(text, "/air/index/"))
        {
            return "01 Flight Search";
        }

        return null;
    }

    /// <summary>Cruise web page names and cruise-core paths from the v2 journey query.</summary>
    private static string? Cruise(string text)
    {
        if (Has(text, "AuthenticateTmo")) return "01 T-Mobile SSO";
        if (Has(text, "cruise-listing") || Has(text, "DestinationSearch")
            || Has(text, "/cruisesearch") || Has(text, "/getcustomerid") || Has(text, "/search"))
        {
            return "02 Cruise Listing";
        }

        if (Has(text, "cruise-detail") || Has(text, "ProductDetails") || Has(text, "/itinerary")
            || Has(text, "/cruiseitinerary"))
        {
            return "03 Cruise Detail";
        }

        if (Has(text, "rooms-rates") || Has(text, "RoomsandRates") || Has(text, "/category")
            || Has(text, "/cruisecategory"))
        {
            return "04 Rooms & Rates";
        }

        if (Has(text, "cruisecabinonhold") || Has(text, "cabin-selection") || Has(text, "SelectaRoom")
            || Has(text, "/cabin"))
        {
            return "06 Cabin Selection";
        }

        if (Has(text, "total-price") || Has(text, "PriceReview") || Has(text, "/pricing")
            || Has(text, "/cruisecabin"))
        {
            return "05 Total Price";
        }

        if (Has(text, "passenger")) return "07 Passenger";
        if (Has(text, "/options") || Has(text, "option")) return "08 Options";
        if (Has(text, "payment")) return "09 Payment";
        if (Has(text, "BookingReview") || Has(text, "/review") || Has(text, "review")) return "10 Review";
        if (Has(text, "confirm") || Has(text, "booking")) return "11 Confirmation";
        return null;
    }

    private static bool Has(string text, string marker) =>
        text.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
