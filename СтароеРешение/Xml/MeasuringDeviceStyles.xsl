<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<!--<xsl:template match="*[starts-with(@xsi:type,'ct:Тип.Базовый.Прибор')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.Прибор')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
		</xsl:element>
	</xsl:template>

</xsl:stylesheet>