# AsyncUdpServer
A simple to use UDP server that can asynchronously send packets and asynchronously receive packets.
There is an option to handle received packets asynchronously (handle multiple packets at the same time)
It will queue sends and receives if you reach the set limit of the amount that can be handled async.
