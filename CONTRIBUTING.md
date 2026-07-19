How to submit changes
=====================

Please try to follow the guidelines below. They will make things
easier on the maintainers. Not all of these guidelines matter for every
trivial change so apply some common sense.

If you are unsure about something written here, ask on the XCP-ng forum https://xcp-ng.org/forum/

0.    Before starting a big project, discuss it on the forum first :-)

1.    Always test your changes, however small, by both targeted
      manual testing and by running the unit tests:

          dotnet test XenCenterLib.Tests/XenCenterLib.Tests.csproj -c Release

      GitHub Actions on the `development` branch also runs restore, Release/Debug
      builds, and these unit tests on every push and pull request.

2.	  When adding new functionality, include test cases for any
	  * important; or
	  * difficult to manually test; or
	  * easy to break
	  new code. Prefer non-UI tests in `XenCenterLib.Tests` (or a future
	  model test project) for security-sensitive helpers.

3.    Make your patch(es) available by creating one or more GitHub pull requests.
      Each pull request should be separately reviewable and mergeable. Only patches
      which must be committed together should be in the same pull request.
      Target the `development` branch; it is the only supported integration line.

4.    Each patch should include a descriptive commit comment that helps
      understand why the patch is necessary and why it works. This will
      be used both for initial review and for new people to understand
      how the code works later.

5.    For bonus points, ensure the project still builds in between every
      patch in a set: this helps hunt down future regressions with 'bisect'.

6.    Make sure you have the right to submit any changes you make. If you
      do changes at work you may find your employer owns the patches
      instead of you.

----------------------------------------------------------------------------

For a list of maintainers, please see [MAINTAINERS](./MAINTAINERS.md) file.
For modernization scope and non-goals, see [MODERNIZATION.md](./MODERNIZATION.md).
