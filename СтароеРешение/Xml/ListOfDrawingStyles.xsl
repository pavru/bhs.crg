<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:cfg="urn:BimHouse:BomUtilConfig" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:import href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/BimHouseFunctions.xsl"/>

	<!--<xsl:template match="*[starts-with(@xsi:type, 'ct:Тип.Базовый.СписокДокументов')]" mode="presenting">-->
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.СписокДокументов')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
			<xsl:call-template name="CreateDrawTextpresenting">
				<xsl:with-param name="Draw" select="."/>
			</xsl:call-template>
		</xsl:element>
	</xsl:template>

	<xsl:template name="CreateDrawTextpresenting">
		<xsl:param name="Draw"/>
		<xsl:variable name="DocNodesCount" select="count($Draw/ct:Документы/ct:Документ)"/>
		<xsl:variable name="DocRefCount" select="count($Draw/ct:Документы/ct:Ведомость )"/>
		<xsl:choose>
			<xsl:when test="$DocNodesCount &gt; 0">
				<xsl:element name="Представления" namespace="urn:BimHouse:CommonDataType">
					<xsl:for-each select="$Draw/ct:Документы/ct:Документы/ct:Документ">
						<xsl:element name="ct:Список" namespace="urn:BimHouse:CommonDataType">
							<xsl:element name="Текст" namespace="urn:BimHouse:CommonDataType">
								<xsl:value-of select="ct:ТипДокумента"/>
								<xsl:text> №&#160;</xsl:text>
								<xsl:value-of select="ct:НомерДокумента"/>
								<xsl:text> от </xsl:text>
								<xsl:value-of select="format-date(ct:ДатаДокумента, '[D01].[M01].[Y0001]')"/>
								<xsl:text>. </xsl:text>
								<xsl:value-of select="ct:Титул"/>
							</xsl:element>
						</xsl:element>
					</xsl:for-each>
				</xsl:element>
			</xsl:when>
			<xsl:when test="$DocRefCount &gt; 0">
				<xsl:element name="Представления" namespace="urn:BimHouse:CommonDataType">
					<xsl:element name="ct:Список" namespace="urn:BimHouse:CommonDataType">
						<xsl:element name="ct:Текст" namespace="urn:BimHouse:CommonDataType">
							<xsl:value-of select="$Draw/ct:Документы/ct:Ведомость/ct:ТипДокумента"/>
							<xsl:text> №&#160;</xsl:text>
							<xsl:value-of select="$Draw/ct:Документы/ct:Ведомость/ct:НомерДокумента"/>
							<xsl:text> от </xsl:text>
							<xsl:value-of select="format-date($Draw/ct:Документы/ct:Ведомость/ct:ДатаДокумента, '[D01].[M01].[Y0001]')"/>
							<xsl:text>. </xsl:text>
							<xsl:value-of select="$Draw/ct:Документы/ct:Ведомость/ct:Титул"/>
						</xsl:element>
					</xsl:element>
				</xsl:element>
			</xsl:when>
		</xsl:choose>
	</xsl:template>
	
</xsl:stylesheet>