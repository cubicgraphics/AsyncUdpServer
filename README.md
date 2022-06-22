# AsyncUdpServer
A simple to use UDP server that can asynchronously send packets and asynchronously receive packets.
It can receive and handle new packets to a set amount asynchronously, so make sure you can handle your packets async
It will queue sends and receives if you reach the set limit of the amount that can be handled async.
