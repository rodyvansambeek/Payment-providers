using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.PaymentProviders.Notifications;

namespace TeaCommerce.PaymentProviders.Classic
{
    /// <summary>
    /// The Buckaroo payment provider for TeaCommerce.
    /// 
    /// Version: 
    ///     Buckaroo Payment Engine 3.0 
    ///     HTML/NVP Gateway 1.05 13 Sep 2013
    /// 
    /// Comments:
    ///  This payment provider opens the buckaroo payment gateway where the customer can select their 
    ///  payment method.
    ///  The payment provider does not send a country to Buckaroo, so Buckaroo can figure the language out from the browser settings.
    /// 
    /// Support:
    ///   our.umbraco.org
    ///  
    /// Development:
    ///   Rody van Sambeek
    ///   e-mail: rvansambeek at kresco dot nl
    /// </summary>

    [PaymentProvider("Buckaroo-Payments")]
    public class BuckarooPayments : APaymentProvider
    {
        const string LiveURL = "https://checkout.buckaroo.nl/";
        const string TestURL = "https://testcheckout.buckaroo.nl/";

        const int NumberOfDecimals = 2;

        public override string DocumentationLink { get { return "http://documentation.teacommerce.net/"; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }
        public override bool SupportsCapturingOfPayment { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
                defaultSettings["brq_websitekey"] = string.Empty;
                defaultSettings["secret_key"] = string.Empty;
                defaultSettings["return_url"] = "";
                defaultSettings["return_cancel_url"] = "";
                defaultSettings["payment_description"] = "description";
                defaultSettings["brq_requestedservices"] = string.Empty;
                defaultSettings["brq_excludedservices"] = string.Empty;
                defaultSettings["brq_payment_method"] = string.Empty;
                defaultSettings["test_mode"] = "1";

                // Custom 'switch status on callback' settings
                defaultSettings["awaiting_transfer_update"] = "0";
                defaultSettings["awaiting_transfer_statusid"] = "1";

                // BetaalGarant settings
                defaultSettings["streetPropertyAlias"] = "address";
                defaultSettings["houseNumberPropertyAlias"] = "housenumber";
                defaultSettings["postalCodePropertyAlias"] = "zipcode";
                defaultSettings["cityPropertyAlias"] = "city";

                defaultSettings["shippingStreetPropertyAlias"] = "shipping_address";
                defaultSettings["shippingHouseNumberPropertyAlias"] = "shipping_housenumber";
                defaultSettings["shippingPostalCodePropertyAlias"] = "shipping_zipcode";
                defaultSettings["shippingCityPropertyAlias"] = "shipping_city";

                defaultSettings["sexeAlias"] = "sexe";
                defaultSettings["birthdateAlias"] = "birthdate";
                defaultSettings["ibanAlias"] = "iban";
                defaultSettings["phoneNumberAlias"] = "phonenumber";

                return defaultSettings;
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("return_url", "settings");

            PaymentHtmlForm htmlForm = new PaymentHtmlForm
            {
                Action = GetHtmlMethodUrl(settings)
            };

            string[] settingsToExclude = new[] { "secret_key", "return_url", "return_cancel_url", "payment_description", "test_mode", "awaiting_transfer_statusid", "awaiting_transfer_update",
                                                 "streetPropertyAlias","houseNumberPropertyAlias","postalCodePropertyAlias","cityPropertyAlias","shippingStreetPropertyAlias",
                                                 "shippingHouseNumberPropertyAlias","shippingPostalCodePropertyAlias", "shippingCityPropertyAlias","sexeAlias","birthdateAlias","ibanAlias","phoneNumberAlias"};



            htmlForm.InputFields = settings.Where(i => !settingsToExclude.Contains(i.Key) && !string.IsNullOrEmpty(i.Value)).ToDictionary(i => i.Key, i => i.Value);

            // Amount
            Currency currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
            if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
            {
                throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
            }
            htmlForm.InputFields["brq_currency"] = currency.IsoCode;
            htmlForm.InputFields["brq_amount"] = order.TotalPrice.Value.WithVat.ToString("0.00", CultureInfo.InvariantCulture);

            // Reference to order
            htmlForm.InputFields["brq_invoicenumber"] = order.CartNumber;
            htmlForm.InputFields["add_cartnumber"] = order.CartNumber;

            //Show a nice description when customer arrives in Buckaroo
            if (settings.ContainsKey("payment_description") && settings["payment_description"] != "")
            {
                string paymentDescription = settings["payment_description"];
                if (order.Properties[paymentDescription] != null)
                {
                    htmlForm.InputFields["brq_description"] = order.Properties[paymentDescription];
                }
            }

            // WARN: this gets the URL of the current path as a base path. So you can use relative URL's to the language node of the site.
            string path = HttpContext.Current.Request.Url.GetLeftPart(UriPartial.Authority);
            string baseUrl = path + String.Concat(HttpContext.Current.Request.Url.Segments.Take(2));

            teaCommerceContinueUrl = GetAbsoluteUrl(baseUrl, settings["return_url"]);
            teaCommerceCancelUrl = GetAbsoluteUrl(baseUrl, settings["return_cancel_url"]);

            // Interactions
            htmlForm.InputFields["brq_return"] = teaCommerceContinueUrl;
            htmlForm.InputFields["brq_returncancel"] = teaCommerceCancelUrl;
            htmlForm.InputFields["brq_returnerror"] = teaCommerceCancelUrl;
            htmlForm.InputFields["brq_returnreject"] = teaCommerceCancelUrl;
            htmlForm.InputFields["brq_push"] = teaCommerceCallBackUrl;
            htmlForm.InputFields["brq_pushfailure"] = teaCommerceCallBackUrl;

            // Module info for Buckaroo
            htmlForm.InputFields["brq_module_name"] = "Tea Commerce Payment Provider for Buckaroo Payments";
            htmlForm.InputFields["brq_module_supplier"] = "Kresco";
            htmlForm.InputFields["brq_module_version"] = "1";
            htmlForm.InputFields["brq_platform_name"] = "Umbraco CMS";
            htmlForm.InputFields["brq_platform_version"] = "6";

            if (settings["brq_payment_method"].Equals("ideal", StringComparison.InvariantCultureIgnoreCase))
            {
                string idealIssuer = order.Properties["ideal_issuer"];
                htmlForm.InputFields["brq_service_ideal_issuer"] = idealIssuer;
            }

            if (settings["brq_requestedservices"].Contains("paymentguarantee"))
            {
                // Information for AfterPay
                htmlForm.InputFields["brq_culture"] = currency.CultureName;
                htmlForm.InputFields["brq_service_paymentguarantee_action"] = "PaymentInvitation"; // Default
                string customerId = order.CustomerId.ToString();
                // Generate random guid for anonymous customers
                if (String.IsNullOrWhiteSpace(customerId))
                    customerId = Guid.NewGuid().ToString();

                htmlForm.InputFields["brq_service_paymentguarantee_CustomerCode"] = customerId;
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerGender"] = order.Properties[settings["sexeAlias"]]; // Value, 1 = Male, 2 = Female
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerFirstName"] = order.PaymentInformation.FirstName;
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerBirthDate"] = order.Properties[settings["birthdateAlias"]];
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerLastName"] = order.PaymentInformation.LastName;
                htmlForm.InputFields["brq_service_paymentguarantee_AmountVat"] = order.TotalPrice.Value.Vat.ToString("0.00", CultureInfo.InvariantCulture);
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerInitials"] = order.PaymentInformation.FirstName.Substring(0, 1);
                htmlForm.InputFields["brq_service_paymentguarantee_DateDue"] = string.Format("{0:yyyy-MM-dd}", DateTime.Now.AddDays(14));
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerIban"] = order.Properties[settings["ibanAlias"]];
                htmlForm.InputFields["brq_service_paymentguarantee_CustomerEmail"] = order.Properties["email"];


                // Determine type
                var phonenumber = order.Properties[settings["phoneNumberAlias"]];
                if (phonenumber.StartsWith("06"))
                {
                    htmlForm.InputFields["brq_service_paymentguarantee_MobilePhoneNumber"] = phonenumber;
                }
                else
                {
                    htmlForm.InputFields["brq_service_paymentguarantee_PhoneNumber"] = phonenumber;
                }


                htmlForm.InputFields["brq_service_paymentguarantee_address_AddressType_1"] = "INVOICE";
                htmlForm.InputFields["brq_service_paymentguarantee_address_Street_1"] = order.Properties[settings["streetPropertyAlias"]];
                htmlForm.InputFields["brq_service_paymentguarantee_address_HouseNumber_1"] = order.Properties[settings["houseNumberPropertyAlias"]];
                // Suffix?
                htmlForm.InputFields["brq_service_paymentguarantee_address_HouseNumberSuffix_1"] = string.Empty;
                htmlForm.InputFields["brq_service_paymentguarantee_address_ZipCode_1"] = order.Properties[settings["postalCodePropertyAlias"]];
                htmlForm.InputFields["brq_service_paymentguarantee_address_City_1"] = order.Properties[settings["cityPropertyAlias"]];

                Country currentPaymentCountry = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
                htmlForm.InputFields["brq_service_paymentguarantee_address_Country_1"] = currentPaymentCountry.RegionCode;

                htmlForm.InputFields["brq_service_paymentguarantee_address_AddressType_2"] = "SHIPPING";
                if (!String.IsNullOrWhiteSpace(order.Properties[settings["shippingStreetPropertyAlias"]]))
                {
                    htmlForm.InputFields["brq_service_paymentguarantee_address_Street_2"] = order.Properties[settings["shippingStreetPropertyAlias"]];
                    htmlForm.InputFields["brq_service_paymentguarantee_address_HouseNumber_2"] = order.Properties[settings["shippingHouseNumberPropertyAlias"]];
                    // Suffix?
                    htmlForm.InputFields["brq_service_paymentguarantee_address_HouseNumberSuffix_2"] = string.Empty;
                    htmlForm.InputFields["brq_service_paymentguarantee_address_ZipCode_2"] = order.Properties[settings["shippingPostalCodePropertyAlias"]];
                    htmlForm.InputFields["brq_service_paymentguarantee_address_City_2"] = order.Properties[settings["shippingCityPropertyAlias"]];

                    Country currentShippingCountry = CountryService.Instance.Get(order.StoreId, order.ShipmentInformation.CountryId.Value);
                    htmlForm.InputFields["brq_service_paymentguarantee_address_Country_2"] = currentShippingCountry.RegionCode;
                }
                else
                {
                    htmlForm.InputFields["brq_service_paymentguarantee_address_Street_2"] = order.Properties[settings["streetPropertyAlias"]];
                    htmlForm.InputFields["brq_service_paymentguarantee_address_HouseNumber_2"] = order.Properties[settings["houseNumberPropertyAlias"]];
                    // Suffix?
                    htmlForm.InputFields["brq_service_paymentguarantee_address_ZipCode_2"] = order.Properties[settings["postalCodePropertyAlias"]];
                    htmlForm.InputFields["brq_service_paymentguarantee_address_City_2"] = order.Properties[settings["cityPropertyAlias"]];
                    htmlForm.InputFields["brq_service_paymentguarantee_address_Country_2"] = currentPaymentCountry.RegionCode;
                }
            }

            // Finally generate signature
            htmlForm.InputFields["brq_signature"] = GenerateBuckarooSignature(htmlForm.InputFields, settings);

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("return_url", "settings");

            // WARN: this gets the URL of the current path as a base path. So you can use relative URL's to the language node of the site.
            string path = HttpContext.Current.Request.Url.GetLeftPart(UriPartial.Authority);
            string baseUrl = path + String.Concat(HttpContext.Current.Request.Url.Segments.Take(2));

            return GetAbsoluteUrl(baseUrl, settings["return_url"]);
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("return_cancel_url", "settings");

            // WARN: this gets the URL of the current path as a base path. So you can use relative URL's to the language node of the site.
            string path = HttpContext.Current.Request.Url.GetLeftPart(UriPartial.Authority);
            string baseUrl = path + String.Concat(HttpContext.Current.Request.Url.Segments.Take(2));

            return GetAbsoluteUrl(baseUrl, settings["return_cancel_url"]);
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            order.MustNotBeNull("order");
            request.MustNotBeNull("request");

            CallbackInfo callbackInfo = null;

            try
            {
                Dictionary<string, string> inputFields = new Dictionary<string, string>();

                string brq_statuscode = request.Form["brq_statuscode"];
                string brq_amount = request.Form["brq_amount"];
                string brq_payment = request.Form["brq_payment"];
                string brq_transactions = request.Form["brq_transactions"];
                string brq_transaction_method = request.Form["brq_transaction_method"];
                string brq_signature = request.Form["brq_signature"];
                string brq_statuscode_detail = request.Form["brq_statuscode_detail"];
                string brq_statusmessage = request.Form["brq_statusmessage"];
                long awaiting_transfer_statusid = 1;

                long.TryParse(settings["awaiting_transfer_statusid"], out awaiting_transfer_statusid);

                foreach (string key in request.Form.Keys)
                {
                    if (!key.Equals("brq_signature", StringComparison.InvariantCultureIgnoreCase))
                        inputFields[key] = request.Form[key];
                }

                if (GenerateBuckarooSignature(inputFields, settings).Equals(brq_signature))
                {
                    decimal orderAmount = Math.Round(order.TotalPrice.Value.WithVat, NumberOfDecimals, MidpointRounding.AwayFromZero);

                    switch (brq_statuscode)
                    {
                        case "190": //Success: the transaction has been completed.

                            //callbackInfo = new CallbackInfo(decimal.Parse(brq_amount, CultureInfo.InvariantCulture), brq_transactions, PaymentState.Captured, brq_transaction_method, brq_payment);
                            var buckarooAmount = decimal.Parse(brq_amount, CultureInfo.InvariantCulture);
                            if (Math.Round(buckarooAmount, NumberOfDecimals, MidpointRounding.AwayFromZero) == orderAmount)
                            {
                                callbackInfo = new CallbackInfo(orderAmount, brq_transactions, PaymentState.Captured, brq_transaction_method, brq_payment);
                                LoggingService.Instance.Info<BuckarooPayments>(string.Format("Buckaroo-Payments: Controle: Buckaroo-Payments:{0} ({1}) OrderAmount: {2} ({3})", buckarooAmount, Math.Round(buckarooAmount, NumberOfDecimals, MidpointRounding.AwayFromZero), orderAmount, orderAmount));
                            }
                            else
                            {
                                callbackInfo = new CallbackInfo(orderAmount, brq_transactions, PaymentState.Captured, brq_transaction_method, brq_payment);
                                // Added noticationmanager to catch order amounts that don't match.
                                NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"],
                                    $"Buckaroo-Payments (cart: {order.CartNumber}): Controle: Buckaroo-Payments:{buckarooAmount} ({Math.Round(buckarooAmount, NumberOfDecimals, MidpointRounding.AwayFromZero)}) " +
                                    $"OrderAmount: {orderAmount} ({orderAmount}) do not match!"
                                );
                                //callbackInfo = new CallbackInfo(orderAmount, request["transaction_id"], PaymentState.Error, brq_transaction_method, brq_payment);
                                LoggingService.Instance.Info<BuckarooPayments>(string.Format("Buckaroo-Payments: Controle: Buckaroo-Payments:{0} ({1}) OrderAmount: {2} ({3}) do not match!", buckarooAmount, Math.Round(buckarooAmount, NumberOfDecimals, MidpointRounding.AwayFromZero), orderAmount, orderAmount));
                            }
                            break;
                        case "490": //Failure: the request failed.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));                            
                            break;
                        case "491"://Validation Failure: The request contains errors.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "492"://Technical Error: The request failed due to a technical error.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "690": //Payment rejected. 
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "790"://Pending input: the request has been received, possibly the gateway is waiting for the customer to enter his details.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "791"://Pending Processing: the Payment Engine is processing the transaction.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "792"://Awaiting consumer action (eg. bank transfer)
                            if (settings["awaiting_transfer_update"] == "1")
                            {
                                order.OrderStatusId = awaiting_transfer_statusid;
                                order.Save();
                            }
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment awaiting consumer action, code: " + brq_statuscode));
                            break;
                        case "793"://Pending Processing: Waiting for sufficient balance.
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));
                            NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Payment failed code: {brq_statuscode} - StatusMessage: {brq_statusmessage}");
                            break;
                        case "890": //Cancelled by consumer
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));                            
                            break;
                        case "891": //Cancelled by merchant
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));                            
                            break;
                        default:
                            LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Payment failed, code: " + brq_statuscode));                            
                            break;
                    }
                }
                else
                {
                    LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ") - Signature not valid"));
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.CartNumber, ")"), exp);
                NotificationManager.MailError(ConfigurationManager.AppSettings["NotificationOnErrors"], $"Buckaroo-Payments cart: {order.CartNumber} - Exception message: {exp.Message}");
            }
            return callbackInfo;
        }

        public override ApiInfo RefundPayment(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("brq_websitekey", "settings");
                settings.MustContainKey("secret_key", "settings");
                settings.MustContainKey("test_mode", "settings");

                //Check that the Iso code exists
                Currency currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
                {
                    throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
                }

                // request via the NVP gateway
                Dictionary<string, string> inputFields = new Dictionary<string, string>();
                inputFields["brq_websitekey"] = settings["brq_websitekey"];

                inputFields["brq_invoicenumber"] = order.CartNumber;
                inputFields["brq_currency"] = currency.IsoCode;
                inputFields["brq_culture"] = currency.CultureName;
                inputFields["brq_amount_credit"] = order.TotalPrice.Value.WithVat.ToString("0.00", CultureInfo.InvariantCulture);
                inputFields["brq_originaltransaction"] = order.TransactionInformation.TransactionId;

                Dictionary<string, string> response = MakeNVPGatewayRequest("TransactionRequest", inputFields, settings);
                if (response.ContainsKey("BRQ_STATUSCODE") && response.ContainsKey("BRQ_TRANSACTIONS"))
                {
                    // set the transaction id to the transaction that contains the refund (important)
                    // just return 'Refunded' (if you use 'PendingExternalSystem' the status 'Refunded' will not be shown in TeaCommerce)
                    apiInfo = new ApiInfo(response["BRQ_TRANSACTIONS"], PaymentState.Refunded);
                }

                LoggingService.Instance.Info<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.OrderNumber, ") - Refunded Order from back-end"));
                return apiInfo;
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.OrderNumber, ")"), exp);
            }
            return apiInfo;
        }

        public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("brq_websitekey", "settings");
                settings.MustContainKey("secret_key", "settings");
                settings.MustContainKey("test_mode", "settings");

                IDictionary<string, string> inputFields = new Dictionary<string, string>();
                inputFields["brq_websitekey"] = settings["brq_websitekey"];
                inputFields["brq_transaction"] = order.TransactionInformation.TransactionId;

                Dictionary<string, string> response = MakeNVPGatewayRequest("TransactionStatus", inputFields, settings);

                /// todo: handle Refunds initated from Payment Plaza
                // these are not recognized here

                if (response.ContainsKey("BRQ_STATUSCODE"))
                {
                    if (response.ContainsKey("BRQ_RELATEDTRANSACTION_REFUND"))
                    {
                        if (response["BRQ_STATUSCODE"].Equals("190"))
                            apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Refunded);
                    }
                    else
                    {
                        if (response["BRQ_STATUSCODE"].Equals("190"))
                            apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Captured);
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<BuckarooPayments>(String.Concat("Buckaroo-Payments (", order.OrderNumber, ")"), exp);
            }

            return apiInfo;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "brq_websitekey":
                    return settingsKey + "<br/><small>Website key from Payment Plaza</small>";
                case "secret_key":
                    return settingsKey + "<br/><small>Secret key from Payment Plaza</small>";
                case "brq_payment_method":
                    return settingsKey + "<br/><small>eg. mastercard, visa, Amex, maestro, Vpay or visaelectron</small>";
                case "return_url":
                    return settingsKey + "<br/><small>The return URL eg. http://domain.com/continue/ </small>";
                case "return_cancel_url":
                    return settingsKey + "<br/><small>The cancel URL eg. http://domain.com/cancel/ </small>";
                case "payment_description":
                    return settingsKey + "<br/><small>Order property to show in Payment Plaza</small>";
                case "brq_requestedservices":
                    return settingsKey + "<br/><small>Comma separated list with requested services</small>";
                case "brq_excludedservices":
                    return settingsKey + "<br/><small>Comma separated list with excluded services</small>";
                case "test_mode":
                    return settingsKey + "<br/><small>1 = true; 0 = false</small>";
                case "awaiting_transfer_update":
                    return settingsKey + "<br/><small>Enbale OrderStatus update (transfer) enabled 1 = true, 0 = false</small>";
                case "awaiting_transfer_statusid":
                    return settingsKey + "<br/><small>TeaCommerce status for Awaiting Customer (792)</small>";
                case "streetPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for street</small>";
                case "houseNumberPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for housenumber</small>";
                case "postalCodePropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for postal code</small>";
                case "cityPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for city</small>";
                case "shippingStreetPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for shipping street</small>";
                case "shippingHouseNumberPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for shipping housenumber</small>";
                case "shippingPostalCodePropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for shipping postal code</small>";
                case "shippingCityPropertyAlias":
                    return settingsKey + "<br/><small>Custom Order field for shipping city</small>";
                case "sexeAlias":
                    return settingsKey + "<br/><small>Custom Order field for sexe (1 for Male, 2 for Female)</small>";
                case "birthdateAlias":
                    return settingsKey + "<br/><small>Custom Order field for birthdate (format yyyy-DD-mm)</small>";
                case "ibanAlias":
                    return settingsKey + "<br/><small>Custom Order field for IBAN</small>";
                case "phoneNumberAlias":
                    return settingsKey + "<br/><small>Custom Order field for phonenumber</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        #region Helper methods

        protected string GenerateBuckarooSignature(IDictionary<string, string> inputFields, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("inputFields");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("secret_key", "settings");

            var fieldsForSignature = inputFields.Where(i => i.Key.StartsWith("brq", StringComparison.InvariantCultureIgnoreCase) || i.Key.StartsWith("add", StringComparison.InvariantCultureIgnoreCase) || i.Key.StartsWith("cust", StringComparison.InvariantCultureIgnoreCase));

            string strToHash = string.Join("", fieldsForSignature.OrderBy(i => i.Key, StringComparer.CurrentCultureIgnoreCase).Select(i => i.Key + "=" + i.Value));
            //string digest = this.GenerateSHA1Hash(String.Concat(strToHash, ssettings["secret_key"]));
            string digest = new SHA1Managed().ComputeHash(Encoding.UTF8.GetBytes(String.Concat(strToHash, settings["secret_key"]))).ToHex();

            return digest;
        }

        protected Dictionary<string, string> MakeNVPGatewayRequest(string operation, IDictionary<string, string> inputFields, IDictionary<string, string> settings)
        {
            Dictionary<string, string> response = new Dictionary<string, string>();

            settings.MustNotBeNull("settings");
            settings.MustContainKey("brq_websitekey", "settings");
            settings.MustContainKey("secret_key", "settings");
            settings.MustContainKey("test_mode", "settings");

            try
            {
                // prepare request
                inputFields["brq_signature"] = GenerateBuckarooSignature(inputFields, settings);

                // prepare response
                string brq_signature = "";
                Dictionary<string, string> nvpResponse = new Dictionary<string, string>();

                string nvpAll = MakePostRequest(GetNvpMethodUrl(settings) + "?op=" + operation, inputFields);

                if (!(nvpAll.Contains("BRQ_APIRESULT") && nvpAll.Contains("Fail")))
                {

                    foreach (string nvp in nvpAll.Split('&'))
                    {
                        string key = nvp.Split('=')[0];
                        string value = HttpUtility.UrlDecode(nvp.Split('=')[1]);

                        if (!key.Equals("BRQ_SIGNATURE"))
                        {
                            nvpResponse.Add(key, value);
                        }
                        else
                        {
                            brq_signature = value;
                        }
                    }

                    if (GenerateBuckarooSignature(nvpResponse, settings).Equals(brq_signature))
                    {
                        return nvpResponse;
                    }
                    else
                    {
                        LoggingService.Instance.Info<BuckarooPayments>("Buckaroo-Payments MakeNVPGatewayRequest: Invalid Signature");
                    }
                }

            }
            catch (Exception e)
            {
                LoggingService.Instance.Error<BuckarooPayments>(String.Concat("Buckaroo-Payments MakeNVPGatewayRequest response: ", response), e);
            }

            return response;
        }

        protected string GetHtmlMethodUrl(IDictionary<string, string> settings)
        {
            settings.MustContainKey("test_mode", "settings");

            string environment = settings.ContainsKey("test_mode") && settings["test_mode"].Equals("1") ? "test" : "prod";

            switch (environment)
            {
                case "prod":
                    return String.Format("{0}html/", LiveURL);
                case "test":
                    return String.Format("{0}html/", TestURL);
            }

            return string.Empty;
        }

        protected string GetNvpMethodUrl(IDictionary<string, string> settings)
        {
            settings.MustContainKey("test_mode", "settings");

            string environment = settings.ContainsKey("test_mode") && settings["test_mode"].Equals("1") ? "test" : "prod";

            switch (environment)
            {
                case "prod":
                    return String.Format("{0}nvp/", LiveURL);
                case "test":
                    return String.Format("{0}nvp/", TestURL);
            }

            return string.Empty;
        }

        protected string GetAbsoluteUrl(string baseUrl, string url)
        {
            if (!url.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
                return String.Concat(baseUrl, url);
            else
                return url;
        }
        #endregion
    }
}
