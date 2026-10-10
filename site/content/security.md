---
title: Accounts and security
description: Passwords, passkeys, two-step sign-in, signed-in devices, the activity log, and what an admin can do.
---

# Accounts and security

## Signing in

Every page and every API call needs a signed-in reader, apart from a [reading list you share by link](library.md#sharing-a-reading-list). Scripts sign in with an [API token](api.md#api-tokens), which opens the books API but never the account. A sign-in lasts thirty days on that browser, and each one is listed on **Account** under **Signed-in devices**, with its browser and when it was last used. Sign out any of them from there, or **Sign out everywhere else**.

Changing your password signs you out everywhere. Sign-in and sign-up allow ten attempts a minute from one address (`Accounts:SignInsPerMinute`).

## Passkeys

A passkey signs you in with your device's fingerprint, face, or PIN, or a password manager, with no password to type and no code to copy. It counts as both steps of two-step sign-in.

1. On **Account**, under **Passkeys**, name it (*My phone*, say) and choose **Add a passkey**. Your device asks you to confirm.
2. Next time, choose **Sign in with a passkey** on the sign-in page.

Each passkey is listed with when it was added and last used, and **Remove** takes one away. Shelf keeps only the public half of each key.

> [!NOTE]
> Browsers make passkeys only for an `https://` address, or `localhost`. Behind a reverse proxy that hides the address readers use, set `Passkeys:Origin` to it, such as `https://shelf.example.org`.

## Single sign-on

If you already sign in to your self-hosted apps through an OpenID Connect provider, such as Authentik, Authelia, Keycloak, Pocket ID, or Zitadel, Shelf can use it too. The sign-in page then has a **Sign in with…** button.

1. At the provider, make an application (an OAuth2/OpenID client) for Shelf. Its redirect address is your Shelf's address followed by `/signin-oidc`, such as `https://shelf.example.org/signin-oidc`, and it needs the `openid`, `profile`, and `email` scopes.
2. Give Shelf its details: `Oidc:Authority` (the provider's issuer address, the part before `/.well-known/openid-configuration`), `Oidc:ClientId`, and `Oidc:ClientSecret`, and `Oidc:Name` for the button, such as `Authentik`. See [Configuration](configuration.md#single-sign-on).

How readers are matched:

- **A reader already here** signs in with their password once, then chooses **Connect** under **Single sign-on** on **Account**. From then on, either way works.
- **Someone new** gets an account of their own the first time they sign in through the provider, as long as the shelf is taking new accounts (`Accounts:AllowSignUp`, or `Oidc:CreateAccounts` to allow it through the provider only). They are named as the provider calls them, and their email address comes along when the provider says it is verified. Such an account has no password; **Set a password** on **Account** adds one.
- Readers are matched by the provider's own unchanging id for each person, never by email address, so nobody can take over an account by claiming someone else's address at the provider.
- Signing in through the provider skips Shelf's own two-step sign-in: the provider's own second step, if you turn it on there, takes its place.
- **Disconnect** on **Account** removes the connection, once the account has a password to fall back on.

The provider sends readers back with an ordinary link rather than a posted form, so this works over plain `http://` on a home network as well as over `https://`.

## Two-step sign-in

Two-step sign-in asks for a code from an authenticator app after your password.

1. On **Account**, choose **Set up two-step sign-in**, and scan the code with an authenticator app, or type its key in.
2. Enter the six-digit code it shows, and choose **Turn on**.
3. Keep the ten **recovery codes** somewhere safe. Each works once, in place of a code, if you lose the authenticator.

The secret is sealed with the shelf's keys, so a copy of the database alone can't use it. To turn it off, enter a current code.

## The activity log

Shelf keeps a record of:

- sign-ups, and failed sign-ins for names that exist;
- password changes and resets;
- admin rights given or taken, and removed accounts;
- two-step sign-in turned on or off, and passkeys added or removed;
- devices signed out from the list;
- device keys and KOReader passwords made;
- restores, snapshots, and backups taken by hand, and imports from Audiobookshelf.

Every reader sees the lines about their own account under **Recent activity** on **Account**; if something there wasn't you, change your password. An admin reads the whole log on **Readers**, or as JSON at `/admin/audit`. The newest 5,000 entries are kept.

## Admins

The first account is an admin. An admin manages readers from **Readers**:

- **A new password** for a reader who forgot theirs. It signs them out everywhere, turns off their two-step sign-in, and removes their passkeys: the way back in after a lost phone, and a way to shut out anyone who added a passkey of their own.
- **Make admin** for another reader, or take it away. The shelf always keeps at least one admin.
- **Delete** a reader, with their books and files. Books lent to them go back to their owners.

Readers can delete their own account from **Account**, with their password.

### When the only admin is locked out

Reset a password, or make someone an admin, from the command line. Each prints what it did and exits:

```bash
# In Docker
docker exec shelf dotnet Shelf.Api.dll --reset-password "Their Name"
docker exec shelf dotnet Shelf.Api.dll --make-admin "Their Name"

# From source
dotnet run --project src/Shelf.Api -- --reset-password "Their Name"
```

## Closing sign-up

Once your readers have accounts, set `Accounts:AllowSignUp` to `false`. The sign-up page then says sign-up is closed, though the first account can always be made.
