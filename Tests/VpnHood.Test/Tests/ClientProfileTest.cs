﻿using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VpnHood.Client.App.ClientProfiles;
using VpnHood.Common.Exceptions;
using VpnHood.Common.Tokens;

namespace VpnHood.Test.Tests;

[TestClass]
public class ClientProfileTest : TestBase
{
    private int _lastSupportId;
    private Token CreateToken()
    {
        var randomId = Guid.NewGuid();
        var token = new Token {
            Name = "Default Test Server",
            IssuedAt = DateTime.UtcNow,
            SupportId = _lastSupportId++.ToString(),
            TokenId = randomId.ToString(),
            Secret = randomId.ToByteArray(),
            ServerToken = new ServerToken {
                HostEndPoints = [IPEndPoint.Parse("127.0.0.1:443")],
                CertificateHash = randomId.ToByteArray(),
                HostName = randomId.ToString(),
                HostPort = 443,
                Secret = randomId.ToByteArray(),
                CreatedTime = DateTime.UtcNow,
                IsValidHostName = false
            }
        };

        return token;
    }

    [TestMethod]
    public async Task BuiltIn_AccessKeys_initialization()
    {
        var appOptions = TestHelper.CreateAppOptions();
        var tokens = new[] { CreateToken(), CreateToken() };
        appOptions.AccessKeys = tokens.Select(x => x.ToAccessKey()).ToArray();

        await using var app1 = TestHelper.CreateClientApp(appOptions: appOptions);
        var clientProfiles = app1.ClientProfileService.List();
        Assert.AreEqual(tokens.Length, clientProfiles.Length);
        Assert.AreEqual(tokens[0].TokenId, clientProfiles[0].Token.TokenId);
        Assert.AreEqual(tokens[1].TokenId, clientProfiles[1].Token.TokenId);
        Assert.AreEqual(tokens[0].TokenId,
            clientProfiles.Single(x => x.ClientProfileId == app1.Features.BuiltInClientProfileId).Token.TokenId);

        // BuiltIn token should not be removed
        foreach (var clientProfile in clientProfiles) {
            Assert.ThrowsException<UnauthorizedAccessException>(() => {
                // ReSharper disable once AccessToDisposedClosure
                app1.ClientProfileService.Remove(clientProfile.ClientProfileId);
            });
        }
    }

    [TestMethod]
    public async Task BuiltIn_AccessKeys_RemoveOldKeys()
    {
        var appOptions = TestHelper.CreateAppOptions();
        var tokens1 = new[] { CreateToken(), CreateToken() };
        appOptions.AccessKeys = tokens1.Select(x => x.ToAccessKey()).ToArray();

        await using var app1 = TestHelper.CreateClientApp(appOptions: appOptions);
        await app1.DisposeAsync();

        // create app again
        var tokens2 = new[] { CreateToken(), CreateToken() };
        appOptions.AccessKeys = tokens2.Select(x => x.ToAccessKey()).ToArray();
        await using var app2 = TestHelper.CreateClientApp(appOptions: appOptions);

        var clientProfiles = app2.ClientProfileService.List();
        Assert.AreEqual(tokens2.Length, clientProfiles.Length);
        Assert.AreEqual(tokens2[0].TokenId, clientProfiles[0].Token.TokenId);
        Assert.AreEqual(tokens2[1].TokenId, clientProfiles[1].Token.TokenId);
        foreach (var clientProfile in clientProfiles)
            Assert.IsTrue(clientProfile.ClientProfile.IsBuiltIn);
    }

    [TestMethod]
    public async Task ClientPolicy()
    {
        await using var app = TestHelper.CreateClientApp();

        // test two region in a same country
        var token = CreateToken();
        token.Tags = ["#public"];
        var defaultPolicy = new ClientPolicy {
            CountryCode = "*",
            FreeLocations = ["US", "CA"],
            Normal = 10,
            PremiumByPurchase = true,
            PremiumByRewardAd = 20,
            PremiumByTrial = 30
        };
        var caPolicy = new ClientPolicy {
            CountryCode = "CA",
            FreeLocations = ["CA"],
            PremiumByPurchase = true,
            Normal = 200,
            PremiumByTrial = 300
        };

        token.ClientPolicies = [defaultPolicy, caPolicy];

        token.ServerToken.ServerLocations = [
            "US", "US/California",
            "CA/Region1 [#premium]", "CA/Region2",
            "FR/Region1 [#premium]", "FR/Region2 [#premium]"
        ];

        // test free US client
        app.ClientProfileService.Reload("US");
        var clientProfileItem = app.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        var clientProfileInfo = clientProfileItem.ClientProfileInfo;

        // default (*/*)
        var location = clientProfileInfo.ServerLocationInfos.Single(x => x.ServerLocation == "*/*");
        Assert.IsTrue(location.Options.HasFree);
        Assert.IsTrue(location.Options.HasPremium);
        Assert.IsTrue(location.Options.Prompt);
        Assert.AreEqual(defaultPolicy.Normal, location.Options.Normal);
        Assert.AreEqual(defaultPolicy.PremiumByRewardAd, location.Options.PremiumByRewardAd);
        Assert.AreEqual(defaultPolicy.PremiumByTrial, location.Options.PremiumByTrial);

        // (US/*) there is no premium server here
        location = clientProfileInfo.ServerLocationInfos.Single(x => x.ServerLocation == "US/*");
        Assert.IsTrue(location.Options.HasFree);
        Assert.IsFalse(location.Options.HasPremium);
        Assert.IsFalse(location.Options.Prompt);
        Assert.AreEqual(defaultPolicy.Normal, location.Options.Normal);
        Assert.IsNull(location.Options.PremiumByRewardAd);
        Assert.IsNull(location.Options.PremiumByTrial);

        // (FR/*) just premium
        location = clientProfileInfo.ServerLocationInfos.Single(x => x.ServerLocation == "FR/*");
        Assert.IsFalse(location.Options.HasFree);
        Assert.IsTrue(location.Options.HasPremium);
        Assert.IsTrue(location.Options.Prompt);
        Assert.IsNull(location.Options.Normal);
        Assert.AreEqual(defaultPolicy.PremiumByRewardAd, location.Options.PremiumByRewardAd);
        Assert.AreEqual(defaultPolicy.PremiumByTrial, location.Options.PremiumByTrial);

        // (US/*) no free for CA clients
        app.ClientProfileService.Reload("CA");
        clientProfileInfo = app.ClientProfileService.Get(clientProfileInfo.ClientProfileId).ClientProfileInfo;
        location = clientProfileInfo.ServerLocationInfos.Single(x => x.ServerLocation == "US/*");
        Assert.IsFalse(location.Options.HasFree);
        Assert.IsTrue(location.Options.HasPremium);
        Assert.IsTrue(location.Options.Prompt);
        Assert.IsNull(location.Options.Normal);
        Assert.AreEqual(caPolicy.PremiumByRewardAd, location.Options.PremiumByRewardAd);
        Assert.AreEqual(caPolicy.PremiumByTrial, location.Options.PremiumByTrial);

        // create premium token
        token.Tags = [];
        clientProfileItem = app.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        clientProfileInfo = clientProfileItem.ClientProfileInfo;
        location = clientProfileInfo.ServerLocationInfos.Single(x => x.ServerLocation == "FR/*");
        Assert.IsFalse(location.Options.HasFree);
        Assert.IsTrue(location.Options.HasPremium);
        Assert.IsFalse(location.Options.Prompt);
        Assert.AreEqual(0, location.Options.Normal);
        Assert.IsNull(location.Options.PremiumByRewardAd);
        Assert.IsNull(location.Options.PremiumByTrial);
        Assert.IsFalse(location.Options.PremiumByPurchase);
    }


    [TestMethod]
    public async Task Crud()
    {
        await using var app = TestHelper.CreateClientApp();

        // ************
        // *** TEST ***: AddAccessKey should add a clientProfile
        var token1 = CreateToken();
        token1.ServerToken.ServerLocations = ["us", "us/california"];
        var clientProfile1 = app.ClientProfileService.ImportAccessKey(token1.ToAccessKey());
        Assert.IsNotNull(app.ClientProfileService.FindByTokenId(token1.TokenId), "ClientProfile is not added");
        Assert.AreEqual(token1.TokenId, clientProfile1.Token.TokenId,
            "invalid tokenId has been assigned to clientProfile");

        // ************
        // *** TEST ***: AddAccessKey with new accessKey should add another clientProfile
        var token2 = CreateToken();
        app.ClientProfileService.ImportAccessKey(token2.ToAccessKey());
        Assert.IsNotNull(app.ClientProfileService.FindByTokenId(token1.TokenId), "ClientProfile is not added");

        // ************
        // *** TEST ***: AddAccessKey by same accessKey should just update token
        var profileCount = app.ClientProfileService.List().Length;
        token1.Name = "Token 1000";
        app.ClientProfileService.ImportAccessKey(token1.ToAccessKey());
        Assert.AreEqual(token1.Name, app.ClientProfileService.GetToken(token1.TokenId).Name);
        Assert.AreEqual(profileCount, app.ClientProfileService.List().Length);

        // ************
        // *** TEST ***: Update throw NotExistsException exception if tokenId does not exist
        Assert.ThrowsException<NotExistsException>(() => {
            // ReSharper disable once AccessToDisposedClosure
            app.ClientProfileService.Update(Guid.NewGuid(), new ClientProfileUpdateParams {
                ClientProfileName = "Hi"
            });
        });

        // ************
        // *** TEST ***: Update should update the old node if ClientProfileId already exists
        var updateParams = new ClientProfileUpdateParams {
            ClientProfileName = Guid.NewGuid().ToString(),
            IsFavorite = true,
            CustomData = Guid.NewGuid().ToString()
        };
        app.ClientProfileService.Update(clientProfile1.ClientProfileId, updateParams);
        Assert.AreEqual(updateParams.ClientProfileName.Value, app.ClientProfileService.Get(clientProfile1.ClientProfileId).BaseInfo.ClientProfileName);
        Assert.AreEqual(updateParams.IsFavorite.Value, app.ClientProfileService.Get(clientProfile1.ClientProfileId).ClientProfile.IsFavorite);
        Assert.AreEqual(updateParams.CustomData.Value, app.ClientProfileService.Get(clientProfile1.ClientProfileId).ClientProfile.CustomData);

        // ************
        // *** TEST ***: RemoveClientProfile
        app.ClientProfileService.Remove(clientProfile1.ClientProfileId);
        Assert.IsNull(app.ClientProfileService.FindById(clientProfile1.ClientProfileId),
            "ClientProfile has not been removed!");
    }

    [TestMethod]
    public async Task Save_load()
    {
        await using var app1 = TestHelper.CreateClientApp();

        var token1 = CreateToken();
        var clientProfile1 = app1.ClientProfileService.ImportAccessKey(token1.ToAccessKey());

        var token2 = CreateToken();
        var clientProfile2 = app1.ClientProfileService.ImportAccessKey(token2.ToAccessKey());

        var clientProfiles = app1.ClientProfileService.List();
        await app1.DisposeAsync();

        var appOptions = TestHelper.CreateAppOptions();
        appOptions.StorageFolderPath = app1.StorageFolderPath;

        await using var app2 = TestHelper.CreateClientApp(appOptions: appOptions);
        Assert.AreEqual(clientProfiles.Length, app2.ClientProfileService.List().Length,
            "ClientProfiles count are not same!");
        Assert.IsNotNull(app2.ClientProfileService.FindById(clientProfile1.ClientProfileId));
        Assert.IsNotNull(app2.ClientProfileService.FindById(clientProfile2.ClientProfileId));
        Assert.IsNotNull(app2.ClientProfileService.GetToken(token1.TokenId));
        Assert.IsNotNull(app2.ClientProfileService.GetToken(token2.TokenId));
    }


    [TestMethod]
    public async Task Default_ServerLocation()
    {
        await using var app = TestHelper.CreateClientApp();

        // test two region in a same country
        var token = CreateToken();
        token.ServerToken.ServerLocations = ["us/texas [#tag1]", "us/california [#tag1 #tag2]"];

        var clientProfile = app.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        app.UserSettings.ClientProfileId = clientProfile.ClientProfileId;
        app.UserSettings.ServerLocation = "us/*";
        app.Settings.Save();
        Assert.AreEqual("us/*", app.State.ClientServerLocationInfo?.ServerLocation);
        CollectionAssert.AreEquivalent(new[] { "#tag1", "~#tag2" }, app.State.ClientServerLocationInfo?.Tags);
        Assert.IsNull(app.UserSettings.ServerLocation);

        app.UserSettings.ServerLocation = "us/california";
        app.Settings.Save();
        CollectionAssert.AreEquivalent(new[] { "#tag1", "#tag2" }, app.State.ClientServerLocationInfo?.Tags);

        app.UserSettings.ServerLocation = "us/texas";
        app.Settings.Save();
        CollectionAssert.AreEquivalent(new[] { "#tag1", }, app.State.ClientServerLocationInfo?.Tags);

        // test three regin
        token = CreateToken();
        token.ServerToken.ServerLocations = ["us/texas", "us/california [#z1 #z2]", "fr/paris [#p1 #p2]"];
        clientProfile = app.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        app.UserSettings.ClientProfileId = clientProfile.ClientProfileId;
        app.UserSettings.ServerLocation = "fr/paris";
        app.Settings.Save();
        CollectionAssert.AreEquivalent(new[] { "#p1", "#p2" }, app.State.ClientServerLocationInfo?.Tags);

        app.UserSettings.ServerLocation = "*/*";
        app.Settings.Save();
        CollectionAssert.AreEquivalent(new[] { "~#p1", "~#p2", "~#z1", "~#z2" }, app.State.ClientServerLocationInfo?.Tags);
    }

    [TestMethod]
    public async Task ServerLocations()
    {
        await using var app1 = TestHelper.CreateClientApp();

        // test two region in a same country
        var token = CreateToken();
        token.ServerToken.ServerLocations = ["us", "us/california"];
        var clientProfileItem = app1.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        var clientProfileInfo = clientProfileItem.ClientProfileInfo;
        var serverLocations = clientProfileInfo.ServerLocationInfos.Select(x => x.ServerLocation).ToArray();
        var i = 0;
        Assert.AreEqual("us/*", serverLocations[i++]);
        Assert.AreEqual("us/california", serverLocations[i++]);
        Assert.IsFalse(clientProfileInfo.ServerLocationInfos[0].IsNestedCountry);
        Assert.IsTrue(clientProfileInfo.ServerLocationInfos[0].IsDefault);
        Assert.IsTrue(clientProfileInfo.ServerLocationInfos[1].IsNestedCountry);
        Assert.IsFalse(clientProfileInfo.ServerLocationInfos[1].IsDefault);
        _ = i;

        // test multiple countries
        token = CreateToken();
        token.ServerToken.ServerLocations = ["us", "us/california", "uk"];
        clientProfileItem = app1.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        clientProfileInfo = clientProfileItem.ClientProfileInfo;
        serverLocations = clientProfileInfo.ServerLocationInfos.Select(x => x.ServerLocation).ToArray();
        i = 0;
        Assert.AreEqual("*/*", serverLocations[i++]);
        Assert.AreEqual("uk/*", serverLocations[i++]);
        Assert.AreEqual("us/*", serverLocations[i++]);
        Assert.AreEqual("us/california", serverLocations[i++]);
        Assert.IsFalse(clientProfileInfo.ServerLocationInfos[0].IsNestedCountry);
        Assert.IsTrue(clientProfileInfo.ServerLocationInfos[0].IsDefault);
        _ = i;

        // test multiple countries
        token = CreateToken();
        token.ServerToken.ServerLocations = ["us/virgina", "us/california", "uk/england [#pr]", "uk/region2"];
        clientProfileItem = app1.ClientProfileService.ImportAccessKey(token.ToAccessKey());
        clientProfileInfo = clientProfileItem.ClientProfileInfo;
        serverLocations = clientProfileInfo.ServerLocationInfos.Select(x => x.ServerLocation).ToArray();
        i = 0;
        Assert.AreEqual("*/*", serverLocations[i++]);
        Assert.AreEqual("uk/*", serverLocations[i++]);
        Assert.AreEqual("uk/england", serverLocations[i++]);
        Assert.AreEqual("uk/region2", serverLocations[i++]);
        Assert.AreEqual("us/*", serverLocations[i++]);
        Assert.AreEqual("us/california", serverLocations[i++]);
        Assert.AreEqual("us/virgina", serverLocations[i++]);
        Assert.IsFalse(clientProfileInfo.ServerLocationInfos[0].IsNestedCountry);
        Assert.IsFalse(clientProfileInfo.ServerLocationInfos[1].IsNestedCountry);
        Assert.IsTrue(clientProfileInfo.ServerLocationInfos[2].IsNestedCountry);
        Assert.IsTrue(clientProfileInfo.ServerLocationInfos[3].IsNestedCountry);
        _ = i;
    }
}