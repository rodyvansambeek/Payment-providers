﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "2CheckOut" )]
  public class TwoCheckOut : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-2checkout-with-tea-commerce/"; } }

    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "sid" ] = string.Empty;
        defaultSettings[ "lang" ] = "en";
        defaultSettings[ "x_receipt_link_url" ] = string.Empty;
        defaultSettings[ "secretWord" ] = string.Empty;
        defaultSettings[ "streetAddressPropertyAlias" ] = "streetAddress";
        defaultSettings[ "cityPropertyAlias" ] = "city";
        defaultSettings[ "zipCodePropertyAlias" ] = "zipCode";
        defaultSettings[ "phonePropertyAlias" ] = "phone";
        defaultSettings[ "phoneExtensionPropertyAlias" ] = "phoneExtension";
        defaultSettings[ "demo" ] = "Y";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://www.2checkout.com/checkout/spurchase"
      };

      string[] settingsToExclude = new[] { "secretWord", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias", "phonePropertyAlias", "phoneExtensionPropertyAlias", "shipping_firstNamePropertyAlias", "shipping_lastNamePropertyAlias", "shipping_streetAddressPropertyAlias", "shipping_cityPropertyAlias", "shipping_zipCodePropertyAlias" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //cartId
      htmlForm.InputFields[ "cart_order_id" ] = order.CartNumber;

      //amount
      htmlForm.InputFields[ "total" ] = order.TotalPrice.Value.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );

      htmlForm.InputFields[ "x_receipt_link_url" ] = teaCommerceContinueUrl;

      //card_holder_name
      htmlForm.InputFields[ "card_holder_name" ] = order.PaymentInformation.FirstName + " " + order.PaymentInformation.LastName;

      //street_address
      if ( settings.ContainsKey( "streetAddressPropertyAlias" ) ) {
        htmlForm.InputFields[ "street_address" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
      }

      //city
      if ( settings.ContainsKey( "cityPropertyAlias" ) ) {
        htmlForm.InputFields[ "city" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];
      }

      //state
      if ( order.PaymentInformation.CountryRegionId != null ) {
        CountryRegion countryRegion = CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value );
        htmlForm.InputFields[ "state" ] = countryRegion.Name;
      }

      //zip
      if ( settings.ContainsKey( "zipCodePropertyAlias" ) ) {
        htmlForm.InputFields[ "zip" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];
      }

      //country
      htmlForm.InputFields[ "country" ] = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId ).Name;

      //email
      htmlForm.InputFields[ "email" ] = order.PaymentInformation.Email;

      //phone
      if ( settings.ContainsKey( "phonePropertyAlias" ) ) {
        htmlForm.InputFields[ "phone" ] = order.Properties[ settings[ "phonePropertyAlias" ] ];
      }

      //phone_extension
      if ( settings.ContainsKey( "phoneExtensionPropertyAlias" ) ) {
        htmlForm.InputFields[ "phone_extension" ] = order.Properties[ settings[ "phoneExtensionPropertyAlias" ] ];
      }

      //shipping name
      if ( settings.ContainsKey( "shipping_firstNamePropertyAlias" ) && settings.ContainsKey( "shipping_lastNamePropertyAlias" ) ) {
        htmlForm.InputFields[ "ship_name" ] = order.Properties[ settings[ "shipping_firstNamePropertyAlias" ] ] + " " + order.Properties[ settings[ "shipping_lastNamePropertyAlias" ] ];
      }

      //shipping street_address
      if ( settings.ContainsKey( "shipping_streetAddressPropertyAlias" ) ) {
        htmlForm.InputFields[ "ship_street_address" ] = order.Properties[ settings[ "shipping_streetAddressPropertyAlias" ] ];
      }

      //shipping city
      if ( settings.ContainsKey( "shipping_cityPropertyAlias" ) ) {
        htmlForm.InputFields[ "ship_city" ] = order.Properties[ settings[ "shipping_cityPropertyAlias" ] ];
      }

      //shipping state
      if ( order.ShipmentInformation.CountryRegionId != null ) {
        htmlForm.InputFields[ "ship_state" ] = CountryRegionService.Instance.Get( order.StoreId, order.ShipmentInformation.CountryRegionId.Value ).Name;
      }

      //shipping zip
      if ( settings.ContainsKey( "shipping_zipCodePropertyAlias" ) ) {
        htmlForm.InputFields[ "ship_zip" ] = order.Properties[ settings[ "shipping_zipCodePropertyAlias" ] ];
      }

      //shipping country
      if ( order.ShipmentInformation.CountryId != null ) {
        htmlForm.InputFields[ "ship_country" ] = CountryService.Instance.Get( order.StoreId, order.ShipmentInformation.CountryId.Value ).Name;
      }

      //fixed
      htmlForm.InputFields[ "fixed" ] = "Y";

      //skip_landing
      htmlForm.InputFields[ "skip_landing" ] = "1";

      //Testing
      if ( htmlForm.InputFields.ContainsKey( "demo" ) && htmlForm.InputFields[ "demo" ] != "Y" )
        htmlForm.InputFields.Remove( "demo" );

      //fixed
      htmlForm.InputFields[ "id_type" ] = "1";

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "x_receipt_link_url", "settings" );

      return settings[ "x_receipt_link_url" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      return "";
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "secretWord", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "demo" ) && settings[ "demo" ] == "Y" ) {
          LogRequest<TwoCheckOut>( request, logGetData: true );
        }

        string accountNumber = request.QueryString[ "sid" ];
        string transaction = request.QueryString[ "order_number" ];
        string strAmount = request.QueryString[ "total" ];
        string key = request.QueryString[ "key" ];

        string md5CheckValue = string.Empty;
        md5CheckValue += settings[ "secretWord" ];
        md5CheckValue += accountNumber;
        md5CheckValue += settings.ContainsKey( "demo" ) && settings[ "demo" ] == "Y" ? "1" : transaction;
        md5CheckValue += strAmount;

        string calculatedMd5 = GenerateMD5Hash( md5CheckValue ).ToUpperInvariant();

        if ( calculatedMd5 == key ) {
          decimal totalAmount = decimal.Parse( strAmount, CultureInfo.InvariantCulture );

          callbackInfo = new CallbackInfo( totalAmount, transaction, PaymentState.Authorized );
        } else {
          LoggingService.Instance.Warn<TwoCheckOut>( "2CheckOut(" + order.CartNumber + ") - MD5Sum security check failed - key: " + key + " - calculatedMD5: " + calculatedMd5 );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<TwoCheckOut>( "2CheckOut(" + order.CartNumber + ") - Process callback", exp );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "x_receipt_link_url":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "demo":
          return settingsKey + "<br/><small>Y = true; N = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
