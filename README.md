# Cosmosis
A thin but opinionated Microsoft.Azure.Cosmos wrapper designed for data and runtime safety.

## How to use
[Examples go here]

## Design philosophy
### The problem

* Entity Framework fundamentally doesn't map to how Cosmos works serverside. EF is RDBMS-shaped and CosmosDB isn't.
* Microsoft.Azure.Cosmos is unwieldy to work with because it is unopinionated. You can work with it fine if you focus on doing it the right way, but it's always a balancing act as Microsoft.Azure.Cosmos is quirky.

### The solution

Microsoft.Azure.Cosmos's unopinionated nature makes it a great base library.
Here's how Cosmosis enhances it:
* Cosmos does not have document locking like RDBMSes. Cosmosis has ETag If-Match always enabled when updating. This avoids blindly overwriting another parallel process's data. Cosmosis's interface requires an update-function rather than an updated document. This way, updates may be retried if the ETag If-Match failed.
* LINQ contains many methods that Cosmos does not support. Current versions of Microsoft.Azure.Cosmos will produce runtime errors on unsupported operations. (older versions will pull all data client-side) Cosmosis provides a LINQ-alike constrained query builder that only supports methods that can be translated into a Cosmos query.

## About the author
I'm a developer specializing in Azure and .NET with a focus on resilience patterns and data safety. Currently available for senior developer and hands-on architect roles in or near Copenhagen, Denmark. Please reach out at cosmosis@waitnostartover.com or check out my [portfolio](https://files.waitnostartover.com/portfolio)
